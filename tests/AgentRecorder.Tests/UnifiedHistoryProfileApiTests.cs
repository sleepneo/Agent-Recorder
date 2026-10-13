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
using static AgentRecorder.Tests.UnifiedHistoryProfileFilterTests;

namespace AgentRecorder.Tests;

[Collection("HeadlessHostIntegration")]
public sealed class UnifiedHistoryProfileApiTests : IDisposable
{
    private readonly UnifiedHistoryTestDatabase _db=Database();
    private readonly string? _prior=Environment.GetEnvironmentVariable("AGENT_RECORDER_DATA_DIR");
    private ApiServer? _server;
    private UnifiedHistoryQueryService? _query;
    private HttpClient Start(bool headless=false,string? fault=null)
    {
        Environment.SetEnvironmentVariable("AGENT_RECORDER_DATA_DIR",_db.Root);ApiKeyAuth.InitializeForTesting(_db.Root);
        var audit=new AuditFake();var tray=new HeadlessTrayContext(audit);var engine=new RecordingEngine(audit);engine.SetTray(tray);
        engine.BackendFactory=_=>throw new InvalidOperationException("SECRET_CAPTURE_FORBIDDEN");
        _query=new(_db.Store,_db.Ordinary,headless);
        IUnifiedHistoryGateway? gateway=fault switch
        {
            "missing"=>null,"throwing"=>new FaultGateway(false),"null_page"=>new FaultGateway(true),
            _=>new SqliteUnifiedHistoryGateway(_query,()=> (Sid,Session))
        };
        _server=new ApiServer(engine,audit,tray,null,null,null,null,null,null,null,null,null,0,unifiedHistoryGateway:gateway);_server.Start();
        return new HttpClient(new HttpClientHandler{UseProxy=false}){BaseAddress=new Uri($"http://127.0.0.1:{_server.BoundPort}"),Timeout=TimeSpan.FromSeconds(10)};
    }
    private static void Auth(HttpClient client)=>client.DefaultRequestHeaders.Add("X-Agent-Recorder-Key",ApiKeyAuth.CurrentApiKey);
    private static async Task<JsonElement> Data(HttpResponseMessage response)
    {
        var text=await response.Content.ReadAsStringAsync();using var doc=JsonDocument.Parse(text);Assert.True(doc.RootElement.GetProperty("ok").GetBoolean());Assert.False(string.IsNullOrWhiteSpace(doc.RootElement.GetProperty("request_id").GetString()));return doc.RootElement.GetProperty("data").Clone();
    }
    private static async Task Error(HttpResponseMessage response,HttpStatusCode status,string code)
    {
        Assert.Equal(status,response.StatusCode);var text=await response.Content.ReadAsStringAsync();using var doc=JsonDocument.Parse(text);
        Assert.False(doc.RootElement.GetProperty("ok").GetBoolean());Assert.Equal(code,doc.RootElement.GetProperty("error").GetProperty("code").GetString());
        Assert.False(string.IsNullOrWhiteSpace(doc.RootElement.GetProperty("request_id").GetString()));
        Assert.DoesNotContain("SECRET",text);Assert.DoesNotContain("Sqlite",text);Assert.DoesNotContain(Sid,text);Assert.DoesNotContain(Session,text);Assert.DoesNotContain("items",text);
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task FormalSharedGatewaysAuthenticateBeforeSqlAndExposeExactSnapshotsAndTruthfulCapabilities(bool headless)
    {
        Seed(_db,"a");Seed(_db,"b",version:2);Seed(_db,"case","profile-a",2);Seed(_db,"no-profile",null);
        _db.SeedPlan("p");_db.SeedOccurrence("p","o");_db.SeedFuture("f");var before=_db.Snapshot();
        using var client=Start(headless);var calls=0;_query!.CandidateCommandForTests=(_,_,_)=>calls++;
        using(var denied=await client.GetAsync("/api/v1/history?source=ordinary&profile_id=Profile-A&profile_version=bad"))
        {await Error(denied,HttpStatusCode.Unauthorized,"UNAUTHORIZED");Assert.Equal(0,calls);}
        Auth(client);
        using(var first=await client.GetAsync("/api/v1/history?source=ordinary&profile_id=Profile-A&limit=1"))
        {
            Assert.Equal(HttpStatusCode.OK,first.StatusCode);var data=await Data(first);var item=Assert.Single(data.GetProperty("items").EnumerateArray());Assert.Equal("a",item.GetProperty("recording_id").GetString());Assert.Equal("Profile-A",item.GetProperty("profile_id").GetString());Assert.Equal(1L,item.GetProperty("profile_version").GetInt64());
            Assert.Equal("Profile-A",data.GetProperty("filters").GetProperty("profile_id").GetString());Assert.Equal(JsonValueKind.Null,data.GetProperty("filters").GetProperty("profile_version").ValueKind);
            var cursor=Uri.EscapeDataString(data.GetProperty("next_cursor").GetString()!);
            using var next=await client.GetAsync("/api/v1/history?source=ordinary&profile_id=Profile-A&cursor="+cursor);Assert.Equal("b",Assert.Single((await Data(next)).GetProperty("items").EnumerateArray()).GetProperty("recording_id").GetString());
            foreach(var change in new[]{"profile_id=Other","profile_id=Profile-A&profile_version=1","profile_id=Profile-A&status=completed","profile_id=Profile-A&created_from=2026-10-05T01:00:00Z"})
            {using var changed=await client.GetAsync("/api/v1/history?source=ordinary&"+change+"&cursor="+cursor);await Error(changed,HttpStatusCode.BadRequest,"INVALID_ARGUMENT");}
        }
        using(var exact=await client.GetAsync("/api/v1/history?source=ordinary&profile_id=Profile-A&profile_version=2&status=completed&created_from=2026-10-05T01:00:00Z&created_before=2026-10-05T01:00:01Z&limit=100"))
        {
            var data=await Data(exact);var item=Assert.Single(data.GetProperty("items").EnumerateArray());Assert.Equal("b",item.GetProperty("recording_id").GetString());Assert.Equal(2,data.GetProperty("filters").GetProperty("profile_version").GetInt64());
            var fields=new[]{"target_key","bundle","media_hash","entry_id","source","entry_kind","source_type","created_at_utc","recording_id","plan_id","occurrence_id","authorization_id","run_id","profile_id","profile_version","status","reason_code","authorization_status","occurrence_status","run_status","run_reason_code","execution_status_code","output_path","output_path_recorded","output_file_exists","output_size_bytes","output_duration_seconds","started_at_utc","completed_at_utc","window_start_utc","window_end_utc"};
            Assert.Equal(fields.Order(),item.EnumerateObject().Select(p=>p.Name).Order());Assert.Equal(JsonValueKind.Null,item.GetProperty("media_hash").ValueKind);Assert.Equal(JsonValueKind.Null,item.GetProperty("bundle").ValueKind);
            var text=await exact.Content.ReadAsStringAsync();foreach(var forbidden in new[]{"profile_digest","proof","specification","microphone",Sid,Session,"nested_session_id"})Assert.DoesNotContain(forbidden,text);
        }
        using(var unfiltered=await client.GetAsync("/api/v1/history?limit=100"))
        {
            var data=await Data(unfiltered);Assert.Equal(JsonValueKind.Null,data.GetProperty("filters").GetProperty("profile_id").ValueKind);
            foreach(var item in data.GetProperty("items").EnumerateArray().Where(i=>i.GetProperty("source").GetString()!="ordinary"||i.GetProperty("recording_id").GetString()=="no-profile"))
            {Assert.Equal(JsonValueKind.Null,item.GetProperty("profile_id").ValueKind);Assert.Equal(JsonValueKind.Null,item.GetProperty("profile_version").ValueKind);}
            Assert.Equal(headless?4:6,data.GetProperty("items").GetArrayLength());
        }
        using(var cap=await client.GetAsync("/api/v1/capabilities"))
        {
            var h=(await Data(cap)).GetProperty("unified_history");Assert.True(h.GetProperty("supported").GetBoolean());Assert.Equal("ordinary",Assert.Single(h.GetProperty("profile_supported_sources").EnumerateArray()).GetString());Assert.True(h.GetProperty("profile_version_requires_profile_id").GetBoolean());
            Assert.Contains(h.GetProperty("filters").EnumerateArray(),f=>f.GetString()=="profile_id");Assert.Contains(h.GetProperty("filters").EnumerateArray(),f=>f.GetString()=="profile_version");
        }
        Assert.Equal(before,_db.Snapshot());
    }

    public static IEnumerable<object[]> BadQueries => new[]
    {
        "profile_id=p","source=all&profile_id=p","source=plan&profile_id=p","source=future_window&profile_id=p",
        "source=ordinary&profile_version=1","source=ordinary&profile_id=","source=ordinary&profile_id=%20p","source=ordinary&profile_id=p%20",
        "source=ordinary&profile_id=p%00q","source=ordinary&profile_id=p%2Fq","source=ordinary&profile_id="+new string('p',129),
        "source=ordinary&profile_id=p&profile_id=p","source=ordinary&profile_id=p&pro%66ile_id=q","source=ordinary&profile_id=p&PROFILE_ID=q",
        "source=ordinary&profile_id=p&profile_version=1&profile_version=1","source=ordinary&profile_id=p&profile_version=1&profile_%76ersion=2",
        "source=ordinary&profile_id=p&profile_version=0","source=ordinary&profile_id=p&profile_version=-1","source=ordinary&profile_id=p&profile_version=",
        "source=ordinary&profile_id=p&profile_version=%20","source=ordinary&profile_id=p&profile_version=%2B1","source=ordinary&profile_id=p&profile_version=1e2",
        "source=ordinary&profile_id=p&profile_version=1.0","source=ordinary&profile_id=p&profile_version=9223372036854775808","source=ordinary&profile_id=p&profile_version=1%20"
    }.Select(q=>new object[]{q});
    [Theory,MemberData(nameof(BadQueries))]
    public async Task InvalidProfileValuesAndDecodedDuplicateKeysHaveStable400BeforeQuery(string query)
    {
        using var client=Start(headless:true);Auth(client);var calls=0;_query!.CandidateCommandForTests=(_,_,_)=>calls++;
        using var response=await client.GetAsync("/api/v1/history?"+query);await Error(response,HttpStatusCode.BadRequest,"INVALID_ARGUMENT");Assert.Equal(0,calls);
    }

    [Theory]
    [InlineData("missing",null)] [InlineData("Profile-A","9223372036854775807")] [InlineData("Profile-A","0002")]
    [InlineData("x' OR 1=1 --",null)]
    public async Task UnknownValidIdsVersionsAndSqlLookingValuesReturnExactResultsOrEmptyPages(string id,string? version)
    {
        Seed(_db,"r",version:2);using var client=Start();Auth(client);
        using var response=await client.GetAsync("/api/v1/history?source=ordinary&profile_id="+Uri.EscapeDataString(id)+(version is null?"":"&profile_version="+version));
        Assert.Equal(HttpStatusCode.OK,response.StatusCode);var data=await Data(response);Assert.Equal(version=="0002"?1:0,data.GetProperty("items").GetArrayLength());
        Assert.Equal(id,data.GetProperty("filters").GetProperty("profile_id").GetString());
    }

    [Theory]
    [InlineData("missing")] [InlineData("throwing")] [InlineData("null_page")] [InlineData("missing_index")] [InlineData("bad_receipt")] [InlineData("query_failure")]
    public async Task MissingOrFaultedProvidersAndDamagedSchemaUseSafe503WithoutWrites(string fault)
    {
        Seed(_db,"r");using var client=Start(fault:fault);
        if(fault=="missing_index")_db.Execute("DROP INDEX idx_ordinary_recording_history_profile_created;");
        if(fault=="bad_receipt")_db.Execute("UPDATE schema_migrations SET definition_checksum=$c WHERE version=26;",("$c",new string('0',64)));
        if(fault=="query_failure")_query!.CandidateCommandForTests=(_,_,_)=>throw new IOException("SECRET_SQL "+_db.Root+" "+Sid);
        var before=RawSnapshot(_db.Store.DatabasePath);Auth(client);using var response=await client.GetAsync("/api/v1/history?source=ordinary&profile_id=Profile-A");await Error(response,HttpStatusCode.ServiceUnavailable,"HISTORY_UNAVAILABLE");Assert.Equal(before,RawSnapshot(_db.Store.DatabasePath));
        if(fault=="missing")
        {using var cap=await client.GetAsync("/api/v1/capabilities");var h=(await Data(cap)).GetProperty("unified_history");Assert.False(h.GetProperty("supported").GetBoolean());Assert.Empty(h.GetProperty("profile_supported_sources").EnumerateArray());}
    }

    [Fact]
    public async Task RetentionRejectsNewProfileParametersAndOldStatuslessHistoryCursorStillWorksOverHttp()
    {
        Seed(_db,"a");Seed(_db,"b");using var client=Start();Auth(client);
        var legacy=LegacyCursor(_db.Page(1,new("ordinary")).NextCursor!);
        using var history=await client.GetAsync("/api/v1/history?source=ordinary&cursor="+Uri.EscapeDataString(legacy));Assert.Equal("b",Assert.Single((await Data(history)).GetProperty("items").EnumerateArray()).GetProperty("recording_id").GetString());
        foreach(var p in new[]{"profile_id=Profile-A","profile_version=2"})
        {using var response=await client.GetAsync("/api/v1/retention/preview?completed_before=2026-10-05T03:00:00Z&"+p);await Error(response,HttpStatusCode.BadRequest,"INVALID_ARGUMENT");}
    }

    public void Dispose(){_server?.Stop();ApiKeyAuth.ResetForTesting(null);Environment.SetEnvironmentVariable("AGENT_RECORDER_DATA_DIR",_prior);_db.Dispose();}
    private sealed class AuditFake:AuditLogger{public override void Log(string evt,object payload){}}
    private sealed class FaultGateway(bool nullPage):IUnifiedHistoryGateway
    {
        public IReadOnlyList<string> SupportedSources=>new[]{"ordinary"};
        public UnifiedHistoryPage GetPage(int limit,UnifiedHistoryFilters filters,string? cursor)=>nullPage?null!:throw new IOException("SECRET_SQL "+Sid);
    }
}
