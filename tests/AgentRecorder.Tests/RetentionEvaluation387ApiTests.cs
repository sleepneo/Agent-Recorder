using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using AgentRecorder.Api;
using AgentRecorder.Core;
using AgentRecorder.Infrastructure;
using AgentRecorder.Headless;
using AgentRecorder.Persistence;
using Xunit;
using static AgentRecorder.Tests.UnifiedHistoryTestDatabase;

namespace AgentRecorder.Tests;

[Collection("HeadlessHostIntegration")]
public sealed class RetentionEvaluation387ApiTests
{
    private sealed class Tray(bool headless) : ITrayContext
    {
        internal int UiRequests;
        public string HostMode=>headless?"headless":"tray";public bool SupportsRegionSelectionUi=>false;
        public void RequestConfirmation(RecordingConfirmationPresentation p,Action<ConfirmationDecision> c){UiRequests++;throw new InvalidOperationException("No approval UI allowed");}
        public void RequestRegionSelection(int t,Action<string,int,int,int,int,string,string> c){UiRequests++;throw new InvalidOperationException("No capture UI allowed");}
        public void SetRecording(RecordingUiPresentation p){}public void SetIdle(RecordingUiPresentation p){}public void SetAllIdle(){}public void ShowError(string text){}
    }
    private sealed class Observer(IRetentionEvaluationGateway inner) : IRetentionEvaluationGateway
    {
        internal readonly TaskCompletionSource<RetentionEvaluationResult?> Returned=new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal bool CancelledAtReturn;
        public async Task<RetentionEvaluationResult?> ReadAsync(IReadOnlyList<string> ids,DateTimeOffset before,CancellationToken cancellation)
        {var result=await inner.ReadAsync(ids,before,cancellation);CancelledAtReturn=cancellation.IsCancellationRequested;Returned.TrySetResult(result);return result;}
    }
    private sealed class Harness : IDisposable
    {
        internal readonly RetentionPreviewQueryTests.Fixture F=new();
        private readonly string? _prior=Environment.GetEnvironmentVariable("AGENT_RECORDER_DATA_DIR");
        internal readonly RecordingEngine Engine;internal readonly ApiServer Server;internal readonly HttpClient Client;
        internal readonly RetentionEvaluationService Service;internal readonly Observer Gateway;internal readonly Tray Tray;internal int Captures,Reads;
        internal Harness(bool configured=true,bool auth=true,bool headless=false,RuntimeReadiness? readiness=null)
        {
            Environment.SetEnvironmentVariable("AGENT_RECORDER_DATA_DIR",F.Db.Root);ApiKeyAuth.InitializeForTesting(F.Db.Root);
            var audit=new RetentionSharedOutputPathTests.QuietAudit();Engine=new(audit);Tray=new(headless);Engine.SetTray(Tray);
            Engine.BackendFactory=_=>{Captures++;throw new InvalidOperationException("Forbidden capture");};
            Service=new((sid,session,ids,token)=>{Reads++;return F.Db.Query.GetOrdinaryEvidenceByIds(sid,session,ids,token);},F.Service);
            Gateway=new(new SqliteRetentionEvaluationGateway(Service,()=>(Sid,Session)));
            Server=new(Engine,audit,Tray,readiness,null,null,null,null,null,null,null,null,listenPort:0,
                retentionPreviewGateway:new SqliteRetentionPreviewGateway(F.Service,()=>(Sid,Session)),
                retentionEvaluationGateway:configured?Gateway:null);
            Server.Start();Client=new(new HttpClientHandler {UseProxy=false}) {BaseAddress=new($"http://127.0.0.1:{Server.BoundPort}"),Timeout=TimeSpan.FromSeconds(8)};
            if(auth)Client.DefaultRequestHeaders.Add("X-Agent-Recorder-Key",ApiKeyAuth.CurrentApiKey);
        }
        internal Task<HttpResponseMessage> Post(string body,string suffix="")=>Client.PostAsync("/api/v1/retention/evaluations"+suffix,new StringContent(body,Encoding.UTF8,"application/json"));
        public void Dispose(){Client.Dispose();Server.Stop();Engine.Dispose();ApiKeyAuth.ResetForTesting(null);Environment.SetEnvironmentVariable("AGENT_RECORDER_DATA_DIR",_prior);F.Dispose();}
    }
    private const string Valid="{\"recording_ids\":[\"old\"],\"completed_before\":\"2026-10-05T02:00:00Z\"}";
    public static IEnumerable<object[]> InvalidBodies => new[] {
        "", "null", "[]", "{}", "{\"recording_ids\":[\"a\"]}",
        "{\"recording_ids\":null,\"completed_before\":\"2026-10-05T02:00:00Z\"}",
        "{\"recording_ids\":[],\"completed_before\":\"2026-10-05T02:00:00Z\"}",
        Valid.Replace("[\"old\"]","[\"old\",\"old\"]"),Valid.Replace("[\"old\"]","[null]"),Valid.Replace("[\"old\"]","[5]"),
        Valid.Replace("\"old\"","\"a\\u0000b\""),Valid.Replace("\"old\"","\"a\\nb\""),Valid.Replace("\"old\"","\"x/y\""),
        Valid.Replace("\"old\"","\"x:y\""),Valid.Replace("\"old\"","\"x%y\""),Valid.Replace("\"old\"","\"x?y\""),
        Valid.Replace("\"old\"","\" old\""),Valid.Replace("\"old\"","\""+new string('x',201)+"\""),
        Valid.Replace("2026-10-05T02:00:00Z","2099-01-01T00:00:00Z"),Valid.Replace("2026-10-05T02:00:00Z","2026-10-05T02:00:00"),
        Valid.Replace("2026-10-05T02:00:00Z","2026-10-05T02:00:00+08:00"),Valid.Replace("\"2026-10-05T02:00:00Z\"","null"),
        Valid[..^1]+",\"path\":\"C:\\\\private.mp4\"}",Valid[..^1]+",\"force\":true}",
        Valid[..^1]+",\"sid\":\"foreign\"}",Valid[..^1]+",\"session\":\"foreign\"}",Valid[..^1]+",\"lease\":true}",
        Valid[..^1]+",\"recording_ids\":[\"b\"]}",Valid[..^1]+",\"completed_before\":\"2026-10-05T02:00:00Z\"}",
        Valid.Replace("[\"old\"]","["+string.Join(",",Enumerable.Range(0,21).Select(i=>"\"r"+i+"\""))+"]"),
        Valid.Replace("[\"old\"]","[[[[[\"old\"]]]]]"),new string(' ',16385)+Valid
    }.Select(s=>new object[] {s});

    [Theory,MemberData(nameof(InvalidBodies))]
    public async Task InvalidInputIsRejectedBeforeBusinessReadsOrProbes(string body)
    {
        using var h=new Harness();h.F.Seed("old");var before=h.F.Db.Snapshot();using var response=await h.Post(body);
        Assert.Equal(HttpStatusCode.BadRequest,response.StatusCode);Assert.Equal(0,h.Reads);Assert.Equal(0,h.Captures);Assert.Equal(0,h.Tray.UiRequests);
        RetentionRuntimeOutputTests.NoProbes(h.F);Assert.Equal(before,h.F.Db.Snapshot());
    }
    [Theory][InlineData("?path=x")][InlineData("?completed_before=x")][InlineData("?a=1&a=2")]
    public async Task QueriesAreRejectedBeforeReads(string query)
    {using var h=new Harness();using var response=await h.Post(Valid,query);Assert.Equal(HttpStatusCode.BadRequest,response.StatusCode);Assert.Equal(0,h.Reads);}

    [Theory][InlineData(false)][InlineData(true)]
    public async Task AuthAndCapabilitiesAndOldGetRemainCompatible(bool headless)
    {
        using var h=new Harness(auth:false,headless:headless);h.F.Seed("old");
        using(var response=await h.Post(Valid))Assert.Equal(HttpStatusCode.Unauthorized,response.StatusCode);
        h.Client.DefaultRequestHeaders.Add("X-Agent-Recorder-Key","wrong");
        using(var response=await h.Post(Valid))Assert.Equal(HttpStatusCode.Forbidden,response.StatusCode);Assert.Equal(0,h.Reads);
        h.Client.DefaultRequestHeaders.Remove("X-Agent-Recorder-Key");h.Client.DefaultRequestHeaders.Add("X-Agent-Recorder-Key",ApiKeyAuth.CurrentApiKey);
        using(var response=await h.Client.GetAsync("/api/v1/capabilities"))
        {
            using var json=JsonDocument.Parse(await response.Content.ReadAsStringAsync());var capability=json.RootElement.GetProperty("data").GetProperty("retention_evaluations");
            Assert.True(capability.GetProperty("supported").GetBoolean());Assert.Equal(20,capability.GetProperty("recording_ids_max").GetInt32());
            Assert.False(capability.GetProperty("execution_supported").GetBoolean());Assert.False(capability.GetProperty("reference_protection_ready").GetBoolean());
        }
        using(var response=await h.Post(Valid))Assert.Equal(HttpStatusCode.OK,response.StatusCode);
        using(var response=await h.Client.GetAsync("/api/v1/retention/preview?completed_before=2026-10-05T02:00:00Z"))Assert.Equal(HttpStatusCode.OK,response.StatusCode);
        Assert.Equal(0,h.Captures);
    }
    [Theory][InlineData(false)][InlineData(true)]
    public async Task UnconfiguredOrNotReadyHasNoDefaultEmptyProvider(bool pendingReadiness)
    {
        using var ready=pendingReadiness?new RuntimeReadiness("headless",0):null;
        using var h=new Harness(configured:pendingReadiness,readiness:ready);h.F.Seed("old");using var response=await h.Post(Valid);
        Assert.Equal(HttpStatusCode.ServiceUnavailable,response.StatusCode);Assert.Equal(0,h.Reads);RetentionRuntimeOutputTests.NoProbes(h.F);
    }
    [Fact]
    public async Task TwentyMissingAndCaseDistinctIdsReturnOnlyOwnOrderedFacts()
    {
        using var h=new Harness();h.F.Seed("Case",path:Path.Combine(h.F.Videos,"upper-content.mp4"));h.F.Seed("case",path:Path.Combine(h.F.Videos,"lower-content.mp4"));h.F.Db.SeedFuture("foreign",sid:"SECRET",session:"SECRET");
        var ids=new[] {"missing","Case","foreign","case",new string('x',200)}.Concat(Enumerable.Range(0,15).Select(i=>"r"+i)).ToArray();
        using var response=await h.Post(JsonSerializer.Serialize(new {recording_ids=ids,completed_before="2026-10-05T02:00:00Z"}));
        Assert.Equal(HttpStatusCode.OK,response.StatusCode);var text=await response.Content.ReadAsStringAsync();using var json=JsonDocument.Parse(text);var data=json.RootElement.GetProperty("data");
        Assert.Equal(ids,data.GetProperty("items").EnumerateArray().Select(i=>i.GetProperty("recording_id").GetString()));
        Assert.Equal(18,data.GetProperty("unknown_file_count").GetInt32());Assert.Equal(24,data.GetProperty("observed_candidate_bytes").GetInt64());
        Assert.DoesNotContain("SECRET",text);foreach(var forbidden in new[] {"safe_to_delete","reclaimable_bytes","proposal_id","approval","nonce","file_id","volume_id"})Assert.DoesNotContain(forbidden,text);
    }

    [Theory][InlineData(false)][InlineData(true)]
    public async Task RealHostFactoriesUseActualRuntimeAndExposeNoForeignOwner(bool headless)
    {
        using var h=new Harness(headless:headless);var row=h.F.Seed("old");
        RuntimeOutputTestEngine.Claim(h.Engine,new Recording(false){IsOrdinaryLocalRequest=true,OutputPath=row.OutputPath},row.OutputPath);
        var gateway=headless?AgentRecorder.Headless.Program.CreateRetentionEvaluationGateway(h.F.Db.Query,h.F.Db.Root,h.Engine,()=>(Sid,Session)):
            AgentRecorder.App.Program.CreateRetentionEvaluationGateway(h.F.Db.Query,h.F.Db.Root,h.Engine,()=>(Sid,Session));
        var result=await gateway.ReadAsync(new[] {"old"},h.F.Before,default);Assert.Equal("complete",result!.State);
        Assert.Equal("runtime_output_in_use",Assert.Single(result.Page!.Items).ReasonCode);Assert.Equal(0,h.Captures);Assert.Equal(0,h.Tray.UiRequests);
    }

    [Theory][InlineData(false)][InlineData(true)]
    public async Task ResetAndSendOnlyFinDoNotReleaseBlockedWorker(bool reset)
    {
        using var h=new Harness();h.F.Seed("old");using var release=new ManualResetEventSlim();
        var entered=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);var exited=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var deadline=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);var fin=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        h.F.Files.Observation=path=>{entered.TrySetResult();Assert.True(release.Wait(TimeSpan.FromSeconds(5)));return new RetentionPreviewQueryTests.MetadataFake().Observe(path);};
        h.Service.WorkerExitedForTests=()=>exited.TrySetResult();if(!reset)h.Service.DeadlineForTests=token=>deadline.Task.WaitAsync(token);
        h.Server.ConfigureHostShutdown(()=>HostReadOnlyWorkerState.BusyReason,()=>{});
        h.Server.ArtifactTransportObservedForTests=half=>{if(half)fin.TrySetResult();};
        using var raw=new TcpClient();await raw.ConnectAsync(IPAddress.Loopback,h.Server.BoundPort);
        var bytes=Encoding.UTF8.GetBytes(Valid);var header=Encoding.ASCII.GetBytes($"POST /api/v1/retention/evaluations HTTP/1.1\r\nHost: localhost\r\nX-Agent-Recorder-Key: {ApiKeyAuth.CurrentApiKey}\r\nContent-Length: {bytes.Length}\r\nConnection: close\r\n\r\n");
        await raw.GetStream().WriteAsync(header);await raw.GetStream().WriteAsync(bytes);
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(3));
            if(reset){raw.Client.LingerState=new(true,0);raw.Client.Close(0);}
            else {raw.Client.Shutdown(SocketShutdown.Send);await fin.Task.WaitAsync(TimeSpan.FromSeconds(2));Assert.False(h.Gateway.Returned.Task.IsCompleted);deadline.TrySetResult();}
            var returned=await h.Gateway.Returned.Task.WaitAsync(TimeSpan.FromSeconds(2));Assert.Equal(reset?"cancelled":"timeout",returned!.State);Assert.Null(returned.Page);
            Assert.Equal(reset,h.Gateway.CancelledAtReturn);Assert.Equal("artifact_metadata_worker_pending",HostReadOnlyWorkerState.BusyReason);
            using var busy=await h.Client.PostAsync("/api/v1/host/shutdown",new StringContent("{}"));Assert.Equal(HttpStatusCode.Conflict,busy.StatusCode);
            Assert.Equal("busy",(await h.Service.ReadAsync(new[] {"old"},h.F.Before,Sid,Session))!.State);Assert.Single(h.F.Files.Observations);
        }
        finally {release.Set();await exited.Task.WaitAsync(TimeSpan.FromSeconds(3));}
        Assert.Null(HostReadOnlyWorkerState.BusyReason);Assert.Equal(0,h.Captures);Assert.Equal(0,h.Tray.UiRequests);
    }

    [Theory][InlineData("utf8")][InlineData("oversize")][InlineData("duplicate_length")][InlineData("chunked")]
    public async Task TransportRejectsInvalidUtf8AndBodyBudgetsWithoutBusinessReads(string fault)
    {
        using var h=new Harness();using var raw=new TcpClient();await raw.ConnectAsync(IPAddress.Loopback,h.Server.BoundPort);
        var body=fault=="utf8"?new byte[]{0xff,0xfe}:Encoding.UTF8.GetBytes(Valid);
        var length=fault=="oversize"?16385:body.Length;
        var extra=fault=="duplicate_length"?$"Content-Length: {length}\r\n":fault=="chunked"?"Transfer-Encoding: chunked\r\n":"";
        var header=Encoding.ASCII.GetBytes($"POST /api/v1/retention/evaluations HTTP/1.1\r\nHost: localhost\r\nX-Agent-Recorder-Key: {ApiKeyAuth.CurrentApiKey}\r\nContent-Length: {length}\r\n{extra}Connection: close\r\n\r\n");
        await raw.GetStream().WriteAsync(header);await raw.GetStream().WriteAsync(body);
        using var reader=new StreamReader(raw.GetStream());var response=await reader.ReadToEndAsync().WaitAsync(TimeSpan.FromSeconds(3));
        Assert.StartsWith("HTTP/1.1 400",response);Assert.Equal(0,h.Reads);RetentionRuntimeOutputTests.NoProbes(h.F);
    }
    [Fact]
    public async Task ApiOuterDeadlineCancelsButDoesNotReleaseAProviderIgnoringItsDeadline()
    {
        using var h=new Harness();h.F.Seed("old");using var release=new ManualResetEventSlim();
        var entered=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);var retired=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        h.Service.DeadlineForTests=_=>new TaskCompletionSource().Task;h.Service.WorkerExitedForTests=()=>retired.TrySetResult();
        h.F.Files.Observation=path=>{entered.TrySetResult();Assert.True(release.Wait(TimeSpan.FromSeconds(5)));return new RetentionPreviewQueryTests.MetadataFake().Observe(path);};
        var request=h.Post(Valid);
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(2));using var response=await request.WaitAsync(TimeSpan.FromSeconds(4));
            Assert.Equal(HttpStatusCode.ServiceUnavailable,response.StatusCode);Assert.False(h.Gateway.Returned.Task.IsCompleted);
            Assert.Equal("artifact_metadata_worker_pending",HostReadOnlyWorkerState.BusyReason);Assert.Single(h.F.Files.Observations);
        }
        finally {release.Set();await retired.Task.WaitAsync(TimeSpan.FromSeconds(3));}
        Assert.Equal("cancelled",(await h.Gateway.Returned.Task.WaitAsync(TimeSpan.FromSeconds(2)))!.State);Assert.Null(HostReadOnlyWorkerState.BusyReason);
    }
    [Fact]
    public async Task DisabledApiKeyConfigurationStillRejectsEvaluation()
    {
        using var h=new Harness();ApiKeyAuth.ResetForTesting(null);using var response=await h.Post(Valid);
        Assert.Equal(HttpStatusCode.Unauthorized,response.StatusCode);Assert.Equal(0,h.Reads);RetentionRuntimeOutputTests.NoProbes(h.F);
    }

    [Fact]
    public async Task ExplicitIdsHaveReadOnlyEvaluationEndpoint()
    {
        using var f=new RetentionPreviewQueryTests.Fixture();f.Seed("old");
        var prior=Environment.GetEnvironmentVariable("AGENT_RECORDER_DATA_DIR");
        var audit=new RetentionSharedOutputPathTests.QuietAudit();using var engine=new RecordingEngine(audit);
        var tray=new HeadlessTrayContext(audit);engine.SetTray(tray);ApiServer? server=null;
        try
        {
            Environment.SetEnvironmentVariable("AGENT_RECORDER_DATA_DIR",f.Db.Root);ApiKeyAuth.InitializeForTesting(f.Db.Root);
            server=new(engine,audit,tray,readiness:null,autoStart:null,ffmpegPrewarmer:null,tracer:null,
                ensureContextStore:null,performanceSummaryProvider:null,standingPlanSetupGateway:null,
                recurringPlanSetupGateway:null,planExecutionStatusGateway:null,listenPort:0,
                retentionPreviewGateway:new SqliteRetentionPreviewGateway(f.Service,()=>(Sid,Session)),
                retentionEvaluationGateway:new SqliteRetentionEvaluationGateway(new RetentionEvaluationService(f.Db.Query.GetOrdinaryEvidenceByIds,f.Service),()=>(Sid,Session)));
            server.Start();using var client=new HttpClient(new HttpClientHandler {UseProxy=false}) {BaseAddress=new($"http://127.0.0.1:{server.BoundPort}")};
            client.DefaultRequestHeaders.Add("X-Agent-Recorder-Key",ApiKeyAuth.CurrentApiKey);
            using var response=await client.PostAsync("/api/v1/retention/evaluations",new StringContent("{\"recording_ids\":[\"old\"],\"completed_before\":\"2026-10-05T02:00:00Z\"}",Encoding.UTF8,"application/json"));
            Assert.Equal(HttpStatusCode.OK,response.StatusCode);
            using var json=JsonDocument.Parse(await response.Content.ReadAsStringAsync());var data=json.RootElement.GetProperty("data");
            Assert.True(data.GetProperty("read_only").GetBoolean());Assert.False(data.GetProperty("deletion_supported").GetBoolean());
            Assert.Equal("age_rule_match",Assert.Single(data.GetProperty("items").EnumerateArray()).GetProperty("reason_code").GetString());
        }
        finally {server?.Stop();ApiKeyAuth.ResetForTesting(null);Environment.SetEnvironmentVariable("AGENT_RECORDER_DATA_DIR",prior);}
    }
}
