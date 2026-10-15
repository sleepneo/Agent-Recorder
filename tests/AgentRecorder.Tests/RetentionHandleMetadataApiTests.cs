using System.Net;
using System.Text.Json;
using AgentRecorder.Api;
using AgentRecorder.Core;
using AgentRecorder.Infrastructure;
using AgentRecorder.Headless;
using AgentRecorder.Persistence;
using Microsoft.Data.Sqlite;
using Xunit;
using static AgentRecorder.Tests.UnifiedHistoryTestDatabase;

namespace AgentRecorder.Tests;

[Collection("HeadlessHostIntegration")]
public sealed class RetentionHandleMetadataApiTests : IDisposable
{
    private readonly string? _prior=Environment.GetEnvironmentVariable("AGENT_RECORDER_DATA_DIR");
    private WindowsReadOnlyFileMetadataNativeTests.Files? _files;
    private UnifiedHistoryTestDatabase? _db;
    private ApiServer? _server;
    private RetentionPreviewQueryTests.MetadataFake _synthetic=new();
    private int _reads;
    private HttpClient Start(bool headless,string? observation=null,string? contract=null,bool native=false)
    {
        _files=new();_db=new(_files.Root);var path=_files.Create();if(native&&observation=="hard_link")WindowsReadOnlyFileMetadataNativeTests.HardLink(Path.Combine(_files.Videos,"alias.mp4"),path);
        _db.Ordinary.Register(PublicationAnchorTestFixture.Anchored(_db.Recording("native-output",completed:true) with{OutputPath=path,BundleRegistration=new(null)},native:native));
        Environment.SetEnvironmentVariable("AGENT_RECORDER_DATA_DIR",_db.Root);ApiKeyAuth.InitializeForTesting(_db.Root);
        var query=new UnifiedHistoryQueryService(_db.Store,_db.Ordinary,headless);query.CandidatesSelectedForTests=(_,_)=>_reads++;
        _synthetic.Observation=p=>contract switch{"null"=>null,"enum"=>new((FileObservationOutcome)999),_=>observation=="normal"?RetentionHandleMetadataTests.Good(p):observation=="changed_length"?RetentionHandleMetadataTests.Good(p,13):RetentionHandleMetadataTests.Alter(observation??"hard_link",p)};
        var service=native?new RetentionPreviewQueryService(query,_db.Root, TestRuntimeOutputUse.None):new RetentionPreviewQueryService(query.GetOrdinaryEvidencePage,_db.Root,_synthetic,contract=="provider"?null:_synthetic,()=>At.AddDays(5), TestRuntimeOutputUse.None);
        var audit=new RetentionSharedOutputPathTests.QuietAudit();var tray=new HeadlessTrayContext(audit);var engine=new RecordingEngine(audit);engine.SetTray(tray);engine.BackendFactory=_=>throw new InvalidOperationException("SECRET_CAPTURE");
        _server=new ApiServer(engine,audit,tray,null,null,null,null,null,null,null,null,null,0,retentionPreviewGateway:new SqliteRetentionPreviewGateway(service,()=> (Sid,Session)));_server.Start();
        return new HttpClient(new HttpClientHandler{UseProxy=false}){BaseAddress=new Uri($"http://127.0.0.1:{_server.BoundPort}"),Timeout=TimeSpan.FromSeconds(10)};
    }
    private const string Endpoint="/api/v1/retention/preview?completed_before=2026-10-05T02:00:00Z&limit=1";
    private static void Auth(HttpClient c)=>c.DefaultRequestHeaders.Add("X-Agent-Recorder-Key",ApiKeyAuth.CurrentApiKey);
    private static async Task<JsonElement> Data(HttpResponseMessage response)
    {Assert.Equal(HttpStatusCode.OK,response.StatusCode);using var d=JsonDocument.Parse(await response.Content.ReadAsStringAsync());return d.RootElement.GetProperty("data").Clone();}
    private static void FieldsAndPrivacy(JsonElement data,string raw)
    {
        var item=Assert.Single(data.GetProperty("items").EnumerateArray());var fields=new[]{"entry_id","recording_id","status","completed_at_utc","evaluation","reason_code","age_candidate","recorded_path","recorded_bytes","observed_file_bytes"};Assert.Equal(fields.Order(),item.EnumerateObject().Select(p=>p.Name).Order());
        foreach(var secret in new[]{"SECRET","foreign-secret","volume_serial","\"file_id\"","IdHigh","IdLow","native_handle","BeforePath","AfterPath","\\\\?\\"})Assert.DoesNotContain(secret,raw);
        foreach(var key in new[]{"deletion_supported","reference_protection_ready","physical_identity_protection_ready","bundle_included"})Assert.False(data.GetProperty(key).GetBoolean());
    }
    public static TheoryData<bool,string,string> Reasons=>new()
    {
        {false,"hard_link","hard_linked_output"},{true,"hard_link","hard_linked_output"},{false,"delete_pending","file_identity_changed"},{true,"delete_pending","file_identity_changed"},
        {false,"native_failure","file_identity_unavailable"},{true,"native_failure","file_identity_unavailable"},{false,"outside_path","resolved_path_mismatch"},{true,"outside_path","resolved_path_mismatch"},
        {false,"normal","age_rule_match"},{true,"normal","age_rule_match"},{false,"changed_length","file_length_changed"},{true,"changed_length","file_length_changed"}
    };
    [Theory,MemberData(nameof(Reasons))]
    public async Task GatewaysKeepFieldWhitelistAuthenticationStatisticsAndPrivateIdentity(bool headless,string observation,string reason)
    {
        using var c=Start(headless,observation);using(var denied=await c.GetAsync(Endpoint)){Assert.Equal(HttpStatusCode.Unauthorized,denied.StatusCode);Assert.Equal(0,_reads);Assert.Empty(_synthetic.Observations);}
        Auth(c);var before=_db!.Snapshot();using var response=await c.GetAsync(Endpoint);var data=await Data(response);var raw=await response.Content.ReadAsStringAsync();FieldsAndPrivacy(data,raw);var item=Assert.Single(data.GetProperty("items").EnumerateArray());Assert.Equal(reason,item.GetProperty("reason_code").GetString());Assert.True(item.GetProperty("age_candidate").GetBoolean());
        var reliable=observation is "normal" or "changed_length";Assert.Equal(reliable?0:1,data.GetProperty("unknown_candidate_file_count").GetInt32());Assert.Equal(reliable?(observation=="normal"?12:13):0,data.GetProperty("observed_candidate_bytes").GetInt64());if(!reliable)Assert.Equal(JsonValueKind.Null,item.GetProperty("observed_file_bytes").ValueKind);
        Assert.Equal(before,_db.Snapshot());Assert.Single(_synthetic.Observations);Assert.Empty(_synthetic.Lengths);
    }
    [Theory]
    [InlineData(false,"provider")][InlineData(true,"provider")][InlineData(false,"null")][InlineData(true,"null")][InlineData(false,"enum")][InlineData(true,"enum")]
    public async Task ProbeContractDamageIsSafeWholePage503(bool headless,string fault)
    {
        using var c=Start(headless,contract:fault);Auth(c);var before=_db!.Snapshot();using var response=await c.GetAsync(Endpoint);Assert.Equal(HttpStatusCode.ServiceUnavailable,response.StatusCode);var raw=await response.Content.ReadAsStringAsync();using var d=JsonDocument.Parse(raw);Assert.Equal("RETENTION_PREVIEW_UNAVAILABLE",d.RootElement.GetProperty("error").GetProperty("code").GetString());foreach(var secret in new[]{"items","SECRET","Sqlite","IdHigh","file_id"})Assert.DoesNotContain(secret,raw);Assert.Equal(before,_db.Snapshot());if(fault=="provider"){Assert.Empty(_synthetic.Attributes);Assert.Empty(_synthetic.Observations);}
    }
    [Theory]
    [InlineData(false,false)][InlineData(true,false)][InlineData(false,true)][InlineData(true,true)]
    public async Task BothDefaultHostServicesUseRealWindowsProvider(bool headless,bool hardLink)
    {
        using var c=Start(headless,hardLink?"hard_link":"normal",native:true);Auth(c);var before=_db!.Snapshot();using var response=await c.GetAsync(Endpoint);var data=await Data(response);FieldsAndPrivacy(data,await response.Content.ReadAsStringAsync());Assert.Equal(hardLink?"hard_linked_output":"age_rule_match",Assert.Single(data.GetProperty("items").EnumerateArray()).GetProperty("reason_code").GetString());Assert.Equal(before,_db.Snapshot());Assert.Empty(_synthetic.Observations);
    }
    [Theory]
    [InlineData(false,true)][InlineData(true,true)][InlineData(false,false)][InlineData(true,false)]
    public async Task PublicationReasonsKeepBothHostPrivacyAuthenticationAndUnknownByteContracts(bool headless,bool missing)
    {
        using var c=Start(headless,"normal");
        if(missing)
        {
            _db!.Execute("DELETE FROM ordinary_publication_anchors;"); // explicit legacy fixture
            _synthetic.Attribute=_=>throw new Exception("SECRET unexpected ancestor probe");
            _synthetic.Observation=_=>throw new Exception("SECRET unexpected leaf probe");
        }
        else _synthetic.Observation=p=>{var a=RetentionHandleMetadataTests.Stable();a=a with{Identity=a.Identity! with{IdHigh=a.Identity!.IdHigh^1}};return new(FileObservationOutcome.Sampled,a,a,p,p);};
        using(var denied=await c.GetAsync(Endpoint)){Assert.Equal(HttpStatusCode.Unauthorized,denied.StatusCode);Assert.Equal(0,_reads);}
        Auth(c);var before=_db!.Snapshot();using var response=await c.GetAsync(Endpoint);var data=await Data(response);FieldsAndPrivacy(data,await response.Content.ReadAsStringAsync());
        var item=Assert.Single(data.GetProperty("items").EnumerateArray());Assert.Equal(missing?"publication_identity_missing":"publication_identity_mismatch",item.GetProperty("reason_code").GetString());Assert.Equal(JsonValueKind.Null,item.GetProperty("observed_file_bytes").ValueKind);Assert.Equal(1,data.GetProperty("unknown_candidate_file_count").GetInt32());Assert.Equal(0,data.GetProperty("observed_candidate_bytes").GetInt64());Assert.Equal(before,_db.Snapshot());
        if(missing){Assert.Empty(_synthetic.Attributes);Assert.Empty(_synthetic.Observations);}else Assert.Single(_synthetic.Observations);
    }
    [Theory]
    [InlineData(false)][InlineData(true)]
    public async Task BothHostsFailSafelyOnAnchorAssociationDamageBeforeAnyFileProbe(bool headless)
    {
        using var c=Start(headless,"normal");_db!.Corrupt("UPDATE ordinary_publication_anchors SET output_size_bytes=13;");Auth(c);var before=_db.Snapshot();using var response=await c.GetAsync(Endpoint);Assert.Equal(HttpStatusCode.ServiceUnavailable,response.StatusCode);var raw=await response.Content.ReadAsStringAsync();Assert.Contains("RETENTION_PREVIEW_UNAVAILABLE",raw);foreach(var secret in new[]{"items","Publication","IdHigh","file_id","Sqlite","staging_copy_handle"})Assert.DoesNotContain(secret,raw);Assert.Empty(_synthetic.Attributes);Assert.Empty(_synthetic.Observations);Assert.Equal(before,_db.Snapshot());
    }
    public void Dispose()
    {
        _server?.Stop();ApiKeyAuth.ResetForTesting(null);Environment.SetEnvironmentVariable("AGENT_RECORDER_DATA_DIR",_prior);
        _db?.Ordinary.Dispose();SqliteConnection.ClearAllPools();_files?.Dispose();
    }
}
