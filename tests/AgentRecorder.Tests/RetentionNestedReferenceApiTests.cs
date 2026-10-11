using System.Net;
using System.Text.Json;
using AgentRecorder.Api;
using AgentRecorder.Core;
using AgentRecorder.Headless;
using AgentRecorder.Infrastructure;
using AgentRecorder.Logging;
using AgentRecorder.Persistence;
using Xunit;
using static AgentRecorder.Tests.UnifiedHistoryTestDatabase;

namespace AgentRecorder.Tests;

[Collection("HeadlessHostIntegration")]
public sealed class RetentionNestedReferenceApiTests : IDisposable
{
    private readonly string? _prior=Environment.GetEnvironmentVariable("AGENT_RECORDER_DATA_DIR");
    private ApiServer? _server;
    private RetentionNestedReferenceTests.Fixture? _fixture;
    private HttpClient Start(bool headless=false,bool configured=true)
    {
        _fixture=new(headless){KeepFixtureForDiagnostics=true};Environment.SetEnvironmentVariable("AGENT_RECORDER_DATA_DIR",_fixture.Db.Root);ApiKeyAuth.InitializeForTesting(_fixture.Db.Root);
        var audit=new AuditFake();var tray=new HeadlessTrayContext(audit);var engine=new RecordingEngine(audit);engine.SetTray(tray);engine.BackendFactory=_=>throw new InvalidOperationException("SECRET_CAPTURE_FORBIDDEN");
        // Both actual hosts construct this public service/gateway combination.
        var service=new RetentionPreviewQueryService(_fixture.Query,_fixture.Db.Root);
        _server=new ApiServer(engine,audit,tray,null,null,null,null,null,null,null,null,null,0,retentionPreviewGateway:configured?new SqliteRetentionPreviewGateway(service,()=> (Sid,Session)):null);_server.Start();
        return new HttpClient(new HttpClientHandler{UseProxy=false}){BaseAddress=new Uri($"http://127.0.0.1:{_server.BoundPort}"),Timeout=TimeSpan.FromSeconds(10)};
    }
    private static string Endpoint=>"/api/v1/retention/preview?completed_before="+Uri.EscapeDataString(At.AddHours(1).ToString("O"))+"&limit=1";
    [Theory]
    [InlineData(false)][InlineData(true)]
    public async Task FormalHostScopesAuthenticateBeforeNewEvidenceAndKeepFieldsPrivateAndMediaUnchanged(bool headless)
    {
        using var client=Start(headless);var f=_fixture!;var parent=f.Seed("parent",at:At.AddMinutes(1));f.Seed("SECRET_CHILD_ID",parent:"parent",status:"failed",at:At.AddDays(-1));var calls=0;f.Query.RetentionReferenceCommandForTests=(_,_)=>calls++;
        Directory.CreateDirectory(Path.GetDirectoryName(parent.OutputPath)!);File.WriteAllBytes(parent.OutputPath,new byte[12]);var media=File.ReadAllBytes(parent.OutputPath);var before=f.Db.Snapshot();
        using(var denied=await client.GetAsync(Endpoint)){Assert.Equal(HttpStatusCode.Unauthorized,denied.StatusCode);Assert.Equal(0,calls);}
        client.DefaultRequestHeaders.Add("X-Agent-Recorder-Key",ApiKeyAuth.CurrentApiKey);
        using var response=await client.GetAsync(Endpoint);Assert.Equal(HttpStatusCode.OK,response.StatusCode);var text=await response.Content.ReadAsStringAsync();using var doc=JsonDocument.Parse(text);Assert.True(doc.RootElement.GetProperty("ok").GetBoolean());Assert.NotNull(doc.RootElement.GetProperty("request_id").GetString());
        var data=doc.RootElement.GetProperty("data");var item=Assert.Single(data.GetProperty("items").EnumerateArray());Assert.Equal("nested_parent_referenced",item.GetProperty("reason_code").GetString());Assert.Equal("protected",item.GetProperty("evaluation").GetString());Assert.True(item.GetProperty("age_candidate").GetBoolean());Assert.Equal(JsonValueKind.Null,item.GetProperty("observed_file_bytes").ValueKind);
        Assert.Equal(1,data.GetProperty("age_candidate_count").GetInt32());Assert.Equal(0,data.GetProperty("observed_candidate_bytes").GetInt64());Assert.Equal(1,data.GetProperty("unknown_candidate_file_count").GetInt32());Assert.False(data.GetProperty("observed_bytes_complete").GetBoolean());Assert.False(data.GetProperty("reference_protection_ready").GetBoolean());Assert.False(data.GetProperty("deletion_supported").GetBoolean());
        Assert.DoesNotContain("SECRET_CHILD_ID",text);Assert.DoesNotContain(Sid,text);Assert.DoesNotContain(Session,text);Assert.DoesNotContain("parent_recording_id",text);Assert.DoesNotContain("nested_session_id",text);Assert.DoesNotContain("safe_to_delete",text);Assert.Equal(1,calls);
        Assert.Equal(before,f.Db.Snapshot());Assert.Equal(media,File.ReadAllBytes(parent.OutputPath));Assert.Equal(0,f.OldProbes);
    }
    [Theory]
    [InlineData("missing_provider")][InlineData("missing_index")][InlineData("query_failure")][InlineData("bad_schema")]
    public async Task ReferenceUnavailableUsesExisting503WithoutPrivateFailureText(string fault)
    {
        using var client=Start(configured:fault!="missing_provider");var f=_fixture!;f.Seed("target");
        if(fault=="missing_index")f.Db.Execute("DROP INDEX idx_ordinary_recording_history_parent;");
        if(fault=="bad_schema")f.Db.Execute("UPDATE schema_migrations SET definition_checksum='0000000000000000000000000000000000000000000000000000000000000000' WHERE version=25;");
        if(fault=="query_failure")f.Query.RetentionReferenceCommandForTests=(_,_)=>throw new IOException("SECRET_SQL "+Sid+" PROOF_SECRET");
        var before=f.RawSnapshot();client.DefaultRequestHeaders.Add("X-Agent-Recorder-Key",ApiKeyAuth.CurrentApiKey);using var response=await client.GetAsync(Endpoint);Assert.Equal(HttpStatusCode.ServiceUnavailable,response.StatusCode);var text=await response.Content.ReadAsStringAsync();using var doc=JsonDocument.Parse(text);Assert.Equal("RETENTION_PREVIEW_UNAVAILABLE",doc.RootElement.GetProperty("error").GetProperty("code").GetString());Assert.DoesNotContain("SECRET",text);Assert.DoesNotContain(Sid,text);Assert.DoesNotContain("Sqlite",text);Assert.DoesNotContain("items",text);Assert.Equal(before,f.RawSnapshot());f.NoProbes();
    }
    public void Dispose(){_server?.Stop();ApiKeyAuth.ResetForTesting(null);Environment.SetEnvironmentVariable("AGENT_RECORDER_DATA_DIR",_prior);_fixture?.Dispose();}
    private sealed class AuditFake : AuditLogger {public override void Log(string evt,object payload) { }}
}
