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
using static AgentRecorder.Tests.UnifiedHistorySourcePlanFilterTests;
using AgentRecorder.App;
using AgentRecorder.Core.Automation;
using AgentRecorder.Windows;

namespace AgentRecorder.Tests;

[Collection("HeadlessHostIntegration")]
public sealed class UnifiedHistorySourcePlanApiTests : IDisposable
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
    public async Task AuthenticatedFormalScopesExposeNewFieldsAndTruthfulDiscovery(bool headless)
    {
        foreach(var type in UnifiedHistoryFilterCodes.SourceTypes)Seed(_db,type,type);
        _db.SeedPlan("Plan-A");_db.SeedOccurrence("Plan-A","o");_db.SeedFuture("f");
        var before=_db.Snapshot();using var client=Start(headless);var calls=0;_query!.CandidateCommandForTests=(_,_,_)=>calls++;
        using(var denied=await client.GetAsync("/api/v1/history?source=ordinary&source_type=BAD")){await Error(denied,HttpStatusCode.Unauthorized,"UNAUTHORIZED");Assert.Equal(0,calls);}
        Auth(client);
        foreach(var type in UnifiedHistoryFilterCodes.SourceTypes)
        {
            using var response=await client.GetAsync("/api/v1/history?source=ordinary&source_type="+type+"&limit=1");Assert.Equal(HttpStatusCode.OK,response.StatusCode);
            var data=await Data(response);Assert.Equal(type,data.GetProperty("filters").GetProperty("source_type").GetString());Assert.Equal(JsonValueKind.Null,data.GetProperty("filters").GetProperty("plan_id").ValueKind);
            var item=Assert.Single(data.GetProperty("items").EnumerateArray());Assert.Equal(type,item.GetProperty("source_type").GetString());
            var fields=new[]{"target_key","bundle","media_hash","entry_id","source","entry_kind","source_type","created_at_utc","recording_id","plan_id","occurrence_id","authorization_id","run_id","profile_id","profile_version","status","reason_code","authorization_status","occurrence_status","run_status","run_reason_code","execution_status_code","output_path","output_path_recorded","output_file_exists","output_size_bytes","output_duration_seconds","started_at_utc","completed_at_utc","window_start_utc","window_end_utc"};
            Assert.Equal(fields.Order(),item.EnumerateObject().Select(p=>p.Name).Order());Assert.Equal(JsonValueKind.Null,item.GetProperty("media_hash").ValueKind);Assert.Equal(JsonValueKind.Null,item.GetProperty("bundle").ValueKind);
            var text=await response.Content.ReadAsStringAsync();foreach(var forbidden in new[]{"source_title","HWND","Synthetic source","scope","proof","profile_digest","executable",Sid,Session})Assert.DoesNotContain(forbidden,text);
        }
        using(var plan=await client.GetAsync("/api/v1/history?source=plan&plan_id=Plan-A"))
        {
            if(headless)await Error(plan,HttpStatusCode.BadRequest,"INVALID_ARGUMENT");
            else{var data=await Data(plan);Assert.Equal("Plan-A",data.GetProperty("filters").GetProperty("plan_id").GetString());Assert.Equal(JsonValueKind.Null,Assert.Single(data.GetProperty("items").EnumerateArray()).GetProperty("source_type").ValueKind);}
        }
        using(var all=await client.GetAsync("/api/v1/history?limit=100"))
        {var data=await Data(all);Assert.Equal(headless?4:6,data.GetProperty("items").GetArrayLength());foreach(var i in data.GetProperty("items").EnumerateArray().Where(i=>i.GetProperty("source").GetString()!="ordinary"))Assert.Equal(JsonValueKind.Null,i.GetProperty("source_type").ValueKind);}
        using(var cap=await client.GetAsync("/api/v1/capabilities"))
        {
            var h=(await Data(cap)).GetProperty("unified_history");Assert.Equal(UnifiedHistoryFilterCodes.SourceTypes,h.GetProperty("supported_source_types").EnumerateArray().Select(e=>e.GetString()));
            Assert.Equal("ordinary",Assert.Single(h.GetProperty("source_type_supported_sources").EnumerateArray()).GetString());
            Assert.Equal(headless?Array.Empty<string>():new[]{"plan"},h.GetProperty("plan_id_supported_sources").EnumerateArray().Select(e=>e.GetString()));
            Assert.Contains(h.GetProperty("filters").EnumerateArray(),e=>e.GetString()=="source_type");Assert.Contains(h.GetProperty("filters").EnumerateArray(),e=>e.GetString()=="plan_id");
            Assert.Equal("exact_persisted_recording_category",h.GetProperty("source_type_match").GetString());Assert.Equal("exact_case_sensitive_plan_id_with_current_server_ownership",h.GetProperty("plan_id_match").GetString());
        }
        Assert.Equal(before,_db.Snapshot());
    }

    public static IEnumerable<object[]> BadQueries => new[]{
        "source_type=window","source=all&source_type=window","source=plan&source_type=window","source=future_window&source_type=window",
        "source=ordinary&source_type=","source=ordinary&source_type=Window","source=ordinary&source_type=window,display","source=ordinary&source_type=other",
        "source=ordinary&source_type=%20window","source=ordinary&source_type=window%20","source=ordinary&source_type=window%00",
        "source=ordinary&source_type=window&source_type=window","source=ordinary&source_type=window&source_%74ype=display","source=ordinary&source_type=window&SOURCE_TYPE=display",
        "plan_id=p","source=all&plan_id=p","source=ordinary&plan_id=p","source=future_window&plan_id=p","source=plan&plan_id=",
        "source=plan&plan_id=%20p","source=plan&plan_id=p%20","source=plan&plan_id=p%2Fq","source=plan&plan_id=%C3%A9","source=plan&plan_id=p%00q","source=plan&plan_id="+new string('p',129),
        "source=plan&plan_id=p&plan_id=p","source=plan&plan_id=p&plan_%69d=q","source=plan&plan_id=p&PLAN_ID=q",
        "source=plan&plan_id=p&status=completed","source=plan&plan_id=p&profile_id=p","source=plan&plan_id=p&source_type=window"
    }.Select(q=>new object[]{q});
    [Theory,MemberData(nameof(BadQueries))]
    public async Task InvalidValuesAndDecodedDuplicateKeysRejectBeforeSql(string query)
    {
        using var client=Start();Auth(client);var calls=0;_query!.CandidateCommandForTests=(_,_,_)=>calls++;
        using var response=await client.GetAsync("/api/v1/history?"+query);await Error(response,HttpStatusCode.BadRequest,"INVALID_ARGUMENT");Assert.Equal(0,calls);
    }

    [Fact]
    public async Task ExactPlanQueriesHideUnknownAndForeignCorruptPlansAndNeverLeakPartialPages()
    {
        _db.SeedPlan("Plan-A");_db.SeedOccurrence("Plan-A","a");_db.SeedPlan("plan-a");_db.SeedOccurrence("plan-a","b");
        _db.SeedPlan("foreign","other","other");_db.SeedOccurrence("foreign","bad");_db.Corrupt("UPDATE plans SET status_code='SECRET_CORRUPT' WHERE id='foreign';");
        using var client=Start();Auth(client);var projected=0;_query!.EntriesValidatedForTests=(_,n)=>projected+=n;
        foreach(var (id,count) in new[]{("Plan-A",1),("plan-a",1),("foreign",0),("unknown",0),(new string('p',128),0)})
        {using var response=await client.GetAsync("/api/v1/history?source=plan&plan_id="+id);Assert.Equal(HttpStatusCode.OK,response.StatusCode);Assert.Equal(count,(await Data(response)).GetProperty("items").GetArrayLength());}
        Assert.Equal(2,projected);
        _db.Corrupt("UPDATE plan_history_locator SET plan_id='Plan-A' WHERE occurrence_id='b';");
        using var corrupt=await client.GetAsync("/api/v1/history?source=plan&plan_id=Plan-A&limit=100");await Error(corrupt,HttpStatusCode.ServiceUnavailable,"HISTORY_UNAVAILABLE");
    }

    [Theory]
    [InlineData("missing")] [InlineData("throwing")] [InlineData("null_page")] [InlineData("missing_type_index")] [InlineData("missing_plan_index")] [InlineData("bad_receipt")] [InlineData("query_failure")]
    public async Task ProvidersAndSchemaFaultsAreSafe503WithoutRepair(string fault)
    {
        Seed(_db,"r");using var client=Start(fault:fault);
        if(fault=="missing_type_index")_db.Execute("DROP INDEX idx_ordinary_recording_history_type_created;");
        if(fault=="missing_plan_index")_db.Execute("DROP INDEX idx_plan_history_identity_plan_created;");
        if(fault=="bad_receipt")_db.Execute("UPDATE schema_migrations SET name='SECRET_BAD_RECEIPT' WHERE version=27;");
        if(fault=="query_failure")_query!.CandidateCommandForTests=(_,_,_)=>throw new IOException("SECRET_SQL "+Sid);
        var before=UnifiedHistoryProfileFilterTests.RawSnapshot(_db.Store.DatabasePath);Auth(client);
        using var response=await client.GetAsync("/api/v1/history?source=ordinary&source_type=window");await Error(response,HttpStatusCode.ServiceUnavailable,"HISTORY_UNAVAILABLE");
        Assert.Equal(before,UnifiedHistoryProfileFilterTests.RawSnapshot(_db.Store.DatabasePath));
        if(fault=="missing")
        {using var cap=await client.GetAsync("/api/v1/capabilities");var h=(await Data(cap)).GetProperty("unified_history");Assert.False(h.GetProperty("supported").GetBoolean());foreach(var key in new[]{"source_type_supported_sources","supported_source_types","plan_id_supported_sources"})Assert.Empty(h.GetProperty(key).EnumerateArray());}
    }

    [Theory]
    [InlineData("ordinary")] [InlineData("plan")]
    public async Task HttpCursorsBindNewFiltersAndOldCursorRemainsCompatible(string source)
    {
        foreach(var id in new[]{"a","b","c"})Seed(_db,id);_db.SeedPlan("p");foreach(var id in new[]{"a","b","c"})_db.SeedOccurrence("p",id);
        using var client=Start();Auth(client);var query=source=="ordinary"?"source=ordinary&source_type=window":"source=plan&plan_id=p";
        using var first=await client.GetAsync("/api/v1/history?"+query+"&limit=1");var cursor=(await Data(first)).GetProperty("next_cursor").GetString()!;
        using(var same=await client.GetAsync("/api/v1/history?"+query+"&cursor="+Uri.EscapeDataString(cursor)))Assert.Equal(2,(await Data(same)).GetProperty("items").GetArrayLength());
        using(var changed=await client.GetAsync("/api/v1/history?source="+source+"&cursor="+Uri.EscapeDataString(cursor)))await Error(changed,HttpStatusCode.BadRequest,"INVALID_ARGUMENT");
        var legacy=LegacyCursor(_db.Page(1,new(source)).NextCursor!);
        using(var old=await client.GetAsync("/api/v1/history?source="+source+"&cursor="+Uri.EscapeDataString(legacy)))Assert.Equal(2,(await Data(old)).GetProperty("items").GetArrayLength());
        using(var oldNew=await client.GetAsync("/api/v1/history?"+query+"&cursor="+Uri.EscapeDataString(legacy)))await Error(oldNew,HttpStatusCode.BadRequest,"INVALID_ARGUMENT");
    }

    [Fact]
    public async Task RetentionRejectsBothNewQueryParametersBeforeProviderUse()
    {
        using var client=Start();Auth(client);foreach(var p in new[]{"source_type=window","plan_id=p"})
        {using var response=await client.GetAsync("/api/v1/retention/preview?completed_before=2026-10-05T03:00:00Z&"+p);await Error(response,HttpStatusCode.BadRequest,"INVALID_ARGUMENT");}
    }

    [Fact]
    public async Task RequiredOnceOwnedPlanFilterUsesTheExistingFullEvidenceValidatorWithoutRealUi()
    {
        // Trusted synthetic UI and filesystem readiness; no capture engine or native window.
        var displays=new[]{new StandingLeaseDisplayMetadata("DISPLAY-1","stable-display",DisplayIdentityResolutionStatus.Resolved,new AuthorizedPhysicalRectangle(0,0,1920,1080),96,96,1920,1080,AuthorizedDisplayOrientation.Landscape)};
        using var coordinator=new RequiredOncePlanSetupCoordinator(_db.Store,new AuditFake(),new RequiredUi(),currentDisplaysForTest:()=>displays,
            principalForTest:()=>new RequiredOncePrincipal(Sid,Session),clockForTest:()=>At,outputReadinessForTest:new ReadyOutput());
        var created=coordinator.CreateOrGet(new RequiredOncePlanApiRequest("required-filter",At.AddMinutes(2),At.AddMinutes(3),At.AddMinutes(6),TimeSpan.FromSeconds(60),_db.Root,"required.mp4"));
        await coordinator.WaitForIdleAsync().WaitAsync(TimeSpan.FromSeconds(5));var state=coordinator.Get(created.SetupIntentId!)!;Assert.Equal("scheduled",state.StatusCode);
        _db.SeedPlan("noise");_db.SeedOccurrence("noise","noise");using var client=Start();Auth(client);
        using var response=await client.GetAsync("/api/v1/history?source=plan&plan_id="+Uri.EscapeDataString(state.PlanId!)+"&limit=1");
        Assert.Equal(state.PlanId,Assert.Single((await Data(response)).GetProperty("items").EnumerateArray()).GetProperty("plan_id").GetString());
        Assert.False(File.Exists(Path.Combine(_db.Root,"required.mp4")));
    }
    private sealed class RequiredUi:IRequiredOncePlanSetupUi
    {
        public bool IsInteractiveDesktopAvailable=>true;
        public Task<StandingPlanSetupSelection> SelectFreshRegionAsync(CancellationToken token)=>Task.FromResult(new StandingPlanSetupSelection("selected",120,140,640,360,"DISPLAY-1","virtual_screen"));
        public Task<RequiredOnceCreationApprovalResult> ShowCreationApprovalAsync(RequiredOnceCreationApprovalDetails details,CancellationToken token)=>Task.FromResult(RequiredOnceCreationApprovalResult.Approved);
    }
    private sealed class ReadyOutput:IStandingLeaseOutputReadinessProvider
    {
        public StandingLeaseOutputReadinessResult Check(string directory,string name,TimeSpan duration)
        {var root=StandingLeaseOutputPath.NormalizeDirectory(directory);return StandingLeaseOutputReadinessResult.Ready(new StandingLeaseOutputFileSystemSnapshot(root,Path.GetFullPath(Path.Combine(root,name)),true,false,true,true,long.MaxValue,1));}
    }
    public void Dispose(){_server?.Stop();ApiKeyAuth.ResetForTesting(null);Environment.SetEnvironmentVariable("AGENT_RECORDER_DATA_DIR",_prior);_db.Dispose();}
    private sealed class AuditFake:AuditLogger{public override void Log(string evt,object payload){}}
    private sealed class FaultGateway(bool nullPage):IUnifiedHistoryGateway
    {
        public IReadOnlyList<string> SupportedSources=>new[]{"ordinary"};
        public UnifiedHistoryPage GetPage(int limit,UnifiedHistoryFilters filters,string? cursor)=>nullPage?null!:throw new IOException("SECRET_SQL "+Sid);
    }
}
