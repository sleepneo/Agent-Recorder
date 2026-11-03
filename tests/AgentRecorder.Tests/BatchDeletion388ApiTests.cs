using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using AgentRecorder.Api;
using AgentRecorder.Infrastructure;
using AgentRecorder.Persistence;
using Xunit;
using static AgentRecorder.Tests.BatchDeletion388Fixture;

namespace AgentRecorder.Tests;
[Collection("NonParallel-AgentRecorderDataDir")]
public sealed class BatchDeletion388ApiTests
{
    public static IEnumerable<object[]> InvalidBodies=>new[]{"","null","[]","{}","{\"recording_ids\":null}","{\"recording_ids\":[]}","{\"recording_ids\":\"candidate\"}",
        "{\"recording_ids\":[null]}","{\"recording_ids\":[1]}","{\"recording_ids\":[\"x\",\"x\"]}","{\"recording_ids\":[\"a\",\"b\",\"c\",\"d\",\"e\",\"f\"]}",
        "{\"recording_ids\":[\"x/y\"]}","{\"recording_ids\":[\"a\\u0000b\"]}","{\"recording_ids\":[\"x:y\"]}","{\"recording_ids\":[\"x%y\"]}","{\"recording_ids\":[\" x\"]}",
        "{\"recording_ids\":[\""+new string('x',201)+"\"]}","{\"recording_ids\":[[[[[\"x\"]]]]]}",
        "{\"recording_ids\":[\"candidate\"],\"recording_ids\":[\"r1\"]}","{\"recording_ids\":[\"candidate\"],\"force\":true}","{\"recording_ids\":[\"candidate\"],\"Lease\":\"x\"}",
        "{\"recording_ids\":[\"candidate\"],\"root\":\"C:\\\\Videos\"}","{\"recording_ids\":[\"candidate\"],\"source\":\"ordinary\"}","{\"recording_ids\":[\"candidate\"],\"sid\":\"x\"}",
        "{\"recording_ids\":[\"candidate\"],}","/* comment */{\"recording_ids\":[\"candidate\"]}",new string(' ',4097)+"{}"}.Select(s=>new object[]{s});
    [Theory,MemberData(nameof(InvalidBodies))]
    public async Task StrictBodiesRejectBeforeAnyDispatchOrUi(string body)
    {
        using var f=new BatchDeletion388Fixture();var snapshot=f.F.Db.Snapshot();var result=await f.Post(body:body);
        Assert.Equal(HttpStatusCode.BadRequest,result.Status);Assert.Empty(f.BatchIo.Calls);Assert.Empty(f.ChildIo.Calls);Assert.Equal(0,f.Prompts.Begins);Assert.Equal(0,f.Native.Calls);Assert.Equal(snapshot,f.F.Db.Snapshot());f.PreserveBytes();
    }
    [Theory][InlineData("NO_HEADER")][InlineData("a,b")][InlineData("a/b")][InlineData("a\\b")][InlineData(" ")]
    public async Task InvalidSingleKeyRejectedBeforeDispatch(string key)
    {using var f=new BatchDeletion388Fixture();Assert.Equal(HttpStatusCode.BadRequest,(await f.Post(key)).Status);Assert.Empty(f.BatchIo.Calls);Assert.Equal(0,f.Prompts.Begins);}
    [Theory][InlineData("?force=true")][InlineData("?a=1&a=2")]
    public async Task QueryRejectedBeforeDispatch(string suffix)
    {using var f=new BatchDeletion388Fixture();Assert.Equal(HttpStatusCode.BadRequest,(await f.Post(suffix:suffix)).Status);Assert.Empty(f.BatchIo.Calls);}
    [Fact]
    public async Task AuthCapabilitiesAndOldReadOnlyEvaluationRemainIndependent()
    {
        using var f=new BatchDeletion388Fixture();f.Client.DefaultRequestHeaders.Remove("X-Agent-Recorder-Key");Assert.Equal(HttpStatusCode.Unauthorized,(await f.Post()).Status);
        f.Client.DefaultRequestHeaders.Add("X-Agent-Recorder-Key","wrong");Assert.Equal(HttpStatusCode.Unauthorized,(await f.Post()).Status);
        f.Client.DefaultRequestHeaders.Remove("X-Agent-Recorder-Key");f.Client.DefaultRequestHeaders.Add("X-Agent-Recorder-Key",ApiKeyAuth.CurrentApiKey);
        using var capabilities=JsonDocument.Parse(await f.Client.GetStringAsync("/api/v1/capabilities"));var cap=capabilities.RootElement.GetProperty("data").GetProperty("retention_batch_deletion");
        Assert.Equal(5,cap.GetProperty("recording_ids_max").GetInt32());Assert.True(cap.GetProperty("per_item_local_confirmation").GetBoolean());Assert.False(cap.GetProperty("atomic").GetBoolean());Assert.False(cap.GetProperty("restart_execution_recovery").GetBoolean());
        using var response=await f.Client.PostAsync("/api/v1/retention/evaluations",new StringContent("{\"recording_ids\":[\"candidate\"],\"completed_before\":\"2026-10-08T00:00:00Z\"}",Encoding.UTF8,"application/json"));Assert.Equal(HttpStatusCode.OK,response.StatusCode);
        Assert.Equal(0,f.Prompts.Begins);Assert.Equal(0,f.Native.Calls);
    }
    [Theory][InlineData("headless")][InlineData("null")][InlineData("pending")]
    public async Task UnsupportedCompositionNeverInstallsApprovedProvider(string mode)
    {
        using var ready=mode=="pending"?new RuntimeReadiness("tray",0):null;
        using var f=new BatchDeletion388Fixture(headless:mode=="headless",configured:mode!="null",ready:ready);
        Assert.Equal(HttpStatusCode.ServiceUnavailable,(await f.Post()).Status);Assert.Empty(f.BatchIo.Calls);Assert.Equal(0,f.Prompts.Begins);Assert.Equal(0,f.Native.Calls);
        using var cap=JsonDocument.Parse(await f.Client.GetStringAsync("/api/v1/capabilities"));Assert.False(cap.RootElement.GetProperty("data").GetProperty("retention_batch_deletion").GetProperty("supported").GetBoolean());
    }
    [Fact]
    public async Task AppCompositionSharesActualSingleAuthorityAndHeadlessHasNoBatchInjection()
    {
        using var f=new BatchDeletion388Fixture();var gateway=AgentRecorder.App.Program.CreateRetentionDeletionGateway(f.F.Engine,f.F.Db.Store,f.F.Db.Ordinary,f.F.Db.Root,(UnifiedHistoryTestDatabase.Sid,UnifiedHistoryTestDatabase.Session),default);
        var batch=Assert.IsType<SqliteRetentionDeletionGateway>(gateway).CreateBatchGateway();Assert.True(batch.Supported);Assert.Null(batch.HostShutdownBusyReason);batch.Stop();gateway.Stop();Assert.True(await batch.WaitForIdleAsync());
    }
    [Fact]
    public async Task ForeignOwnerSessionOrRootAndMissingUseSameAbsentProjectionWithoutDispatch()
    {
        using var f=new BatchDeletion388Fixture();var id=await f.Create();var p=await f.Prompts.Next();p.Decide(LocalRetentionUiChoice.Reject);await f.Finish(id);
        foreach(var who in new[]{("foreign",UnifiedHistoryTestDatabase.Session,f.F.Videos),(UnifiedHistoryTestDatabase.Sid,"foreign",f.F.Videos),(UnifiedHistoryTestDatabase.Sid,UnifiedHistoryTestDatabase.Session,Path.Combine(f.F.Videos,"foreign"))})
        {
            using var other=new RetentionBatchDeletionCoordinator(f.F.Db.Store,who.Item1,who.Item2,who.Item3,f.Single,queries:new());Assert.Null(await other.ReadAsync(id));
        }
        using var missing=await f.Client.GetAsync(Endpoint+"/"+new string('f',32));Assert.Equal(HttpStatusCode.NotFound,missing.StatusCode);Assert.Equal(1,f.Prompts.Begins);Assert.Equal(0,f.Native.Calls);
    }
    [Theory][InlineData("utf8")][InlineData("oversize")][InlineData("duplicate_length")][InlineData("duplicate_key")][InlineData("chunked")][InlineData("extra")][InlineData("get_body")][InlineData("get_query")]
    public async Task RawTransportAndGetAreStrictAndBounded(string fault)
    {
        using var f=new BatchDeletion388Fixture();var body=Encoding.UTF8.GetBytes("{\"recording_ids\":[\"candidate\"]}");var method="POST";var path=Endpoint;var extra="";var length=body.Length;
        if(fault=="utf8")body=[0xc3,0x28];if(fault=="oversize")body=Enumerable.Repeat((byte)' ',4097).ToArray();length=body.Length;
        if(fault=="duplicate_length")extra="Content-Length: "+length+"\r\n";
        if(fault=="duplicate_key")extra="Idempotency-Key: other\r\n";
        if(fault=="chunked")extra="Transfer-Encoding: chunked\r\n";
        if(fault=="extra")length--;
        if(fault.StartsWith("get")){method="GET";path+="/"+new string('f',32);if(fault=="get_query"){path+="?force=true";body=[];length=0;}}
        using var tcp=new TcpClient();await tcp.ConnectAsync(IPAddress.Loopback,f.Server.BoundPort);var stream=tcp.GetStream();
        var head=$"{method} {path} HTTP/1.1\r\nHost: localhost\r\nX-Agent-Recorder-Key: {ApiKeyAuth.CurrentApiKey}\r\nIdempotency-Key: raw-key\r\n{extra}Content-Length: {length}\r\n\r\n";
        await stream.WriteAsync(Encoding.UTF8.GetBytes(head));await stream.WriteAsync(body);tcp.Client.Shutdown(SocketShutdown.Send);
        using var reader=new StreamReader(stream,Encoding.UTF8);var text=await reader.ReadToEndAsync().WaitAsync(TimeSpan.FromSeconds(5));Assert.StartsWith("HTTP/1.1 400",text);
        Assert.Empty(f.BatchIo.Calls);Assert.Empty(f.ChildIo.Calls);Assert.Equal(0,f.Native.Calls);Assert.Equal(0,f.Prompts.Begins);
    }
    [Fact]
    public async Task DisabledKeyDoesNotOpenTheBatchEndpoint()
    {using var f=new BatchDeletion388Fixture();ApiKeyAuth.ResetForTesting(null);Assert.Equal(HttpStatusCode.Unauthorized,(await f.Post()).Status);Assert.Empty(f.BatchIo.Calls);}
    [Theory][InlineData(true)][InlineData(false)]
    public async Task HttpResetOrSendOnlyFinDoesNotOwnAnEstablishedBatch(bool reset)
    {
        using var f=new BatchDeletion388Fixture();using var tcp=new TcpClient();await tcp.ConnectAsync(IPAddress.Loopback,f.Server.BoundPort);
        var body=Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new{recording_ids=f.Ids}));var head=$"POST {Endpoint} HTTP/1.1\r\nHost: localhost\r\nX-Agent-Recorder-Key: {ApiKeyAuth.CurrentApiKey}\r\nIdempotency-Key: batch-key\r\nContent-Length: {body.Length}\r\n\r\n";
        await tcp.GetStream().WriteAsync(Encoding.UTF8.GetBytes(head));await tcp.GetStream().WriteAsync(body);var p=await f.Prompts.Next();
        if(reset){tcp.Client.LingerState=new(true,0);tcp.Close();}else tcp.Client.Shutdown(SocketShutdown.Send);
        var replay=await f.Post();Assert.Equal(HttpStatusCode.Accepted,replay.Status);var id=replay.Json.GetProperty("data").GetProperty("batch_deletion_request_id").GetString()!;
        Assert.Equal(1,f.Prompts.Begins);p.Decide(LocalRetentionUiChoice.Approve);var second=await f.Prompts.Next();second.Decide(LocalRetentionUiChoice.Reject);
        var final=await f.Finish(id);Assert.Equal(1,final.DispositionAcceptedCount);Assert.Equal(1,f.Native.Calls);Assert.Equal(2,f.Prompts.Begins);
    }
    [Fact]
    public async Task BatchDrainAlsoWaitsForTheActualOriginalSingleFlowWhenNoParentExists()
    {
        using var f=new BatchDeletion388Fixture();await f.Single.RequestAsync(f.Ids[0],"standalone-single");await f.Prompts.Next();
        var drain=f.Batch.WaitForIdleAsync();Assert.False(drain.IsCompleted);Assert.Equal(f.Single.HostShutdownBusyReason,f.Batch.HostShutdownBusyReason);
        Assert.True(f.Server.Stop(TimeSpan.FromSeconds(5)));Assert.True(await drain.WaitAsync(TimeSpan.FromSeconds(5)));Assert.Equal(0,f.Native.Calls);Assert.Equal(0,f.Scalar("SELECT count(*) FROM ordinary_deletion_batches;"));
    }
}
