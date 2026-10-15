using System.Net;
using System.Reflection;
using System.Text.Json;
using AgentRecorder.Api;
using AgentRecorder.Infrastructure;
using AgentRecorder.Persistence;
using Xunit;

namespace AgentRecorder.Tests;

[Collection("NonParallel-AgentRecorderDataDir")]
public sealed class DeletionRequestApiTests
{
    [Fact]
    public async Task ActualTcpPostPersistsBeforeUiAndReturnsImmediatelyThenGetUsesActualDeletionChain()
    {
        using var f=new DeletionRequestFixture();f.F.Ui.OnBegin=()=>
        {
            Assert.Equal(1,f.Scalar("SELECT count(*) FROM ordinary_deletion_requests WHERE phase='awaiting_confirmation';"));
            Assert.Equal(0,f.Scalar("SELECT count(*) FROM ordinary_deletion_operations;"));Assert.Empty(f.F.Files.Api.Opened);
            using var c=f.F.Writer();using var t=c.BeginTransaction(deferred:false);t.Rollback();
        };
        var id=await f.Create();await f.F.Ui.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));Assert.Equal(0,f.F.Native.Calls);
        var pending=await f.Read(id);Assert.Equal("pending",pending.GetProperty("local_confirmation_state").GetString());Assert.Equal(JsonValueKind.Null,pending.GetProperty("operation_id").ValueKind);Assert.Equal(JsonValueKind.Null,pending.GetProperty("file_action_performed").ValueKind);
        await f.Approve();var final=await f.Finish(id);Assert.Equal("accepted",final.GetProperty("disposition_progress").GetString());Assert.True(final.GetProperty("file_action_performed").GetBoolean());Assert.True(final.GetProperty("original_resources_retired").GetBoolean());Assert.True(final.GetProperty("ledger_resources_retired").GetBoolean());Assert.True(final.GetProperty("our_resources_retired").GetBoolean());Assert.Equal("committed",final.GetProperty("ledger_settlement").GetString());
        Assert.Equal(1,f.F.Native.Calls);Assert.Equal(1,f.F.Ui.Begins);Assert.Equal(1,f.Scalar("SELECT count(*) FROM ordinary_recording_history WHERE status_code='completed';"));
        var replay=await f.Post();Assert.Equal(id,replay.Json.GetProperty("data").GetProperty("deletion_request_id").GetString());Assert.Equal(1,f.F.Ui.Begins);Assert.Equal(1,f.F.Native.Calls);
        foreach(var secret in new[]{"approval_id","key_digest","owner_sid","owner_session","videos_root","nonce","permit","deleted","reclaimed_bytes"})Assert.False(final.TryGetProperty(secret,out _));
    }
    [Fact]
    public async Task ConcurrentSameKeyCreatesOneDurableRequestOneWindowAndOneDisposition()
    {
        using var f=new DeletionRequestFixture();var responses=await Task.WhenAll(Enumerable.Range(0,12).Select(_=>f.Post()));Assert.All(responses,r=>Assert.Equal(HttpStatusCode.Accepted,r.Status));
        var ids=responses.Select(r=>r.Json.GetProperty("data").GetProperty("deletion_request_id").GetString()!).Distinct().ToArray();var id=Assert.Single(ids);
        await f.Approve();await f.Finish(id);Assert.Equal(1,f.F.Ui.Begins);Assert.Equal(1,f.F.Native.Calls);Assert.Equal(1,f.Scalar("SELECT count(*) FROM ordinary_deletion_requests;"));
    }
    [Fact]
    public async Task SameKeyDifferentBodyConflictsAndNewKeyCannotQueueBehindLiveUi()
    {
        using var f=new DeletionRequestFixture();var id=await f.Create();var conflict=await f.Post(body:"{\"recording_id\":\"different\"}");Assert.Equal(HttpStatusCode.Conflict,conflict.Status);
        var busy=await f.Post("different-key");Assert.Equal(HttpStatusCode.ServiceUnavailable,busy.Status);Assert.Contains("busy",busy.Json.GetRawText());Assert.Equal(1,f.Scalar("SELECT count(*) FROM ordinary_deletion_requests;"));
        await f.Approve();await f.Finish(id);Assert.Equal(1,f.F.Ui.Begins);
    }
    [Theory]
    [InlineData("{}")][InlineData("[]")][InlineData("null")][InlineData("{\"recording_id\":1}")][InlineData("{\"recording_id\":null}")]
    [InlineData("{\"recording_id\":\"candidate\",\"approve\":true}")][InlineData("{\"recording_id\":\"candidate\",\"recording_id\":\"candidate\"}")]
    [InlineData("{\"recording_id\":\"candidate\\u0000x\"}")][InlineData("{\"recording_id\":\"../candidate\"}")][InlineData("{\"path\":\"C:\\\\private.mp4\"}")]
    [InlineData("{\"recording_id\":\"candidate\",}")][InlineData("{\"recording_id\":\"candidate\"} trailing")]
    public async Task StrictJsonRejectsUnknownDuplicateMalformedOrExecutableMaterialBeforeDbAndUi(string body)
    {using var f=new DeletionRequestFixture();var r=await f.Post(body:body);Assert.Equal(HttpStatusCode.BadRequest,r.Status);Assert.Empty(f.Database.Calls);Assert.Equal(0,f.F.Ui.Begins);Assert.Equal(0,f.F.Native.Calls);}
    [Theory][InlineData("missing")][InlineData("duplicate")][InlineData("oversized")][InlineData("nul")][InlineData("invalid_utf8")][InlineData("raw_oversized")]
    public async Task ActualRawHttpRejectsHeaderByteAndBodyBoundaries(string kind)
    {
        using var f=new DeletionRequestFixture();var body=kind=="raw_oversized"?new string(' ',2049):"{\"recording_id\":\"candidate\"}";
        var header=kind=="missing"?"":kind=="duplicate"?"Idempotency-Key: a\r\nidempotency-key: a\r\n":"Idempotency-Key: "+(kind=="oversized"?new string('a',129):kind=="nul"?"a\0b":"a")+"\r\n";
        var response=await f.Raw(DeletionRequestFixture.Endpoint,body,header,raw:kind=="invalid_utf8"?new byte[]{0x7b,0xff,0x7d}:null);Assert.StartsWith("HTTP/1.1 400",response);Assert.Empty(f.Database.Calls);Assert.Equal(0,f.F.Native.Calls);
    }
    [Theory][InlineData("/a/b")][InlineData("/%2f")][InlineData("/../candidate")][InlineData("/" )][InlineData("?x=1")][InlineData("/aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa?x=1")]
    public async Task QueryRejectsEncodedMultiSegmentAndExtraQueryWithoutDataAccess(string suffix)
    {using var f=new DeletionRequestFixture();var r=await f.Raw(DeletionRequestFixture.Endpoint+suffix);Assert.StartsWith("HTTP/1.1 400",r);Assert.Empty(f.Database.Calls);}
    [Theory][InlineData(false)][InlineData(true)]
    public async Task ConfiguredKeyAuthenticationPrecedesAllPersistentUiAndNativeWork(bool configured)
    {
        using var f=new DeletionRequestFixture();if(!configured)ApiKeyAuth.ResetForTesting(f.F.Db.Root);
        f.Client.DefaultRequestHeaders.Remove("X-Agent-Recorder-Key");var r=await f.Post();Assert.Equal(HttpStatusCode.Unauthorized,r.Status);Assert.Empty(f.Database.Calls);Assert.Equal(0,f.F.Ui.Begins);
        using var read=await f.Client.GetAsync(DeletionRequestFixture.Endpoint+"/"+new string('a',32));Assert.Equal(HttpStatusCode.Unauthorized,read.StatusCode);
    }
    [Theory][InlineData("headless")][InlineData("not_wired")][InlineData("not_ready")]
    public async Task UnsupportedOrNotReadyHostsAdvertiseFalseAndNeverOpenAuthority(string kind)
    {
        using var ready=kind=="not_ready"?new RuntimeReadiness("tray",0):null;using var f=new DeletionRequestFixture(headless:kind=="headless",configured:kind!="not_wired",readiness:ready);
        var r=await f.Post();Assert.Equal(HttpStatusCode.ServiceUnavailable,r.Status);Assert.Empty(f.Database.Calls);Assert.Equal(0,f.F.Ui.Begins);Assert.Equal(0,f.F.Native.Calls);
        using var response=await f.Client.GetAsync("/api/v1/capabilities");var cap=JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement.GetProperty("data");Assert.False(cap.GetProperty("retention_deletion").GetProperty("supported").GetBoolean());Assert.False(cap.GetProperty("retention_preview").GetProperty("deletion_supported").GetBoolean());
    }
    [Fact]
    public void ActualAppFactoryUsesDefaultLocalUiAndIsCalledByRunWhileHeadlessHasNoDeletionFactory()
    {
        using var f=new LocalRetentionDeletionFixture();using var stopping=new CancellationTokenSource();var type=typeof(AgentRecorder.App.Program);
        var method=type.GetMethod("CreateRetentionDeletionGateway",BindingFlags.NonPublic|BindingFlags.Static)!;
        var calls=(IEnumerable<MethodBase>)typeof(RetentionRuntimeHostTests).GetMethod("Calls",BindingFlags.NonPublic|BindingFlags.Static)!.Invoke(null,new object[]{type.GetMethod("Run",BindingFlags.NonPublic|BindingFlags.Static)!})!;Assert.Contains(method,calls);
        var gateway=AgentRecorder.App.Program.CreateRetentionDeletionGateway(f.Engine,f.Db.Store,f.Db.Ordinary,f.Db.Root,(UnifiedHistoryTestDatabase.Sid,UnifiedHistoryTestDatabase.Session),stopping.Token);
        Assert.IsType<SqliteRetentionDeletionGateway>(gateway);Assert.True(gateway.Supported);gateway.Stop();Assert.False(gateway.Supported);Assert.Null(typeof(AgentRecorder.Headless.Program).GetMethod("CreateRetentionDeletionGateway",BindingFlags.NonPublic|BindingFlags.Static));Assert.Equal(0,f.Native.Calls);
    }
}
