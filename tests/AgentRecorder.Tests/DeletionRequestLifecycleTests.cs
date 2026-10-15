using System.Net;
using System.Text.Json;
using AgentRecorder.Persistence;
using Xunit;
using Phase=AgentRecorder.Tests.OriginalFileHoldFixture.Phase;

namespace AgentRecorder.Tests;

[Collection("NonParallel-AgentRecorderDataDir")]
public sealed class DeletionRequestLifecycleTests
{
    [Theory][InlineData(1)][InlineData(3)][InlineData(2)][InlineData(4)]
    public async Task RejectEscapeCloseAndUnavailableNeverReserveOrCallNative(int choice)
    {
        using var f=new DeletionRequestFixture();var id=await f.Create();await f.F.Ui.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));f.F.Ui.Decide((LocalRetentionUiChoice)choice);f.F.Ui.Retire.TrySetResult(true);var r=await f.Finish(id);
        Assert.Equal(JsonValueKind.Null,r.GetProperty("operation_id").ValueKind);Assert.Equal(JsonValueKind.Null,r.GetProperty("disposition_progress").ValueKind);Assert.Equal(0,f.F.Native.Calls);Assert.Equal(0,f.Scalar("SELECT count(*) FROM ordinary_deletion_requests WHERE approval_id IS NOT NULL;"));Assert.Equal(0,f.Scalar("SELECT count(*) FROM ordinary_deletion_operations;"));
    }
    [Theory][InlineData(false)][InlineData(true)]
    public async Task NoPermitUntilUiActuallyRetiresAndLateUiCannotRestartSixtySecondApproval(bool expire)
    {
        using var f=new DeletionRequestFixture();var id=await f.Create();await f.Approve(false);await Task.Delay(80);Assert.Equal(0,f.F.Native.Calls);Assert.Equal(0,f.Scalar("SELECT count(*) FROM ordinary_deletion_requests WHERE approval_id IS NOT NULL;"));
        if(expire)f.F.Clock.Advance(61);f.F.Ui.Retire.TrySetResult(true);await f.Finish(id);Assert.Equal(expire?0:1,f.F.Native.Calls);
    }
    [Fact]
    public async Task PendingIntentTimeoutCancelsWithoutAnOperationOrAutomaticUiReplay()
    {
        using var f=new DeletionRequestFixture();var id=await f.Create();await f.F.Ui.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));f.F.Clock.Advance(61);f.F.Ui.Retire.TrySetResult(true);await f.Finish(id);
        var replay=await f.Post();Assert.Equal(id,replay.Json.GetProperty("data").GetProperty("deletion_request_id").GetString());Assert.Equal(1,f.F.Ui.Begins);Assert.Equal(0,f.F.Native.Calls);
    }
    [Theory][InlineData("create_before_open")][InlineData("create_before_begin")][InlineData("create_before_sql")][InlineData("create_after_sql")][InlineData("create_before_commit")][InlineData("create_after_commit")]
    public async Task FailedCreationHasNoUiAndDoesNotAutoRetryPersistence(string stage)
    {
        using var f=new DeletionRequestFixture(database:new(){Fault=stage});var r=await f.Post();Assert.Equal(HttpStatusCode.ServiceUnavailable,r.Status);Assert.Equal(0,f.F.Ui.Begins);Assert.Equal(0,f.F.Native.Calls);
        Assert.Equal(1,f.Database.Calls.Count(x=>x.Stage==stage));
    }
    [Theory][InlineData("create")][InlineData("reserve")]
    public async Task CommitReturnUnknownNeverExchangesPersistentStateForAuthorization(string stage)
    {
        using var f=new DeletionRequestFixture(database:new(){UnknownCommit=stage});var r=await f.Post();
        if(stage=="reserve"){Assert.Equal(HttpStatusCode.Accepted,r.Status);await f.Approve();Assert.True(await f.Coordinator.WaitForIdleAsync().WaitAsync(TimeSpan.FromSeconds(5)));}
        else Assert.Equal(HttpStatusCode.ServiceUnavailable,r.Status);
        Assert.Equal(0,f.F.Native.Calls);var replay=await f.Post();Assert.Equal(HttpStatusCode.Accepted,replay.Status);Assert.Equal(stage=="create"?0:1,f.F.Ui.Begins);Assert.Equal(0,f.Scalar("SELECT count(*) FROM ordinary_deletion_operations;"));
    }
    [Theory][InlineData("create")][InlineData("reserve")][InlineData("finish")]
    public async Task UnknownRequestConnectionCloseKeepsBusyAndCannotBeClearedByGet(string stage)
    {
        using var f=new DeletionRequestFixture(database:new(){UnknownClose=stage});var r=await f.Post();
        if(stage!="create"){Assert.Equal(HttpStatusCode.Accepted,r.Status);await f.Approve();}else Assert.Equal(HttpStatusCode.ServiceUnavailable,r.Status);
        Assert.False(await f.Coordinator.WaitForIdleAsync().WaitAsync(TimeSpan.FromSeconds(6)));var other=await f.Post("fresh-key");Assert.Equal(HttpStatusCode.ServiceUnavailable,other.Status);Assert.Equal(stage=="finish"?1:0,f.F.Native.Calls);
    }
    [Theory][InlineData("create_after_commit")][InlineData("reserve_after_commit")][InlineData("finish_after_commit")]
    public async Task PausedPersistenceRetainsOwnershipAndLateCompletionCannotRepeatNativeOrRewriteOldResponse(string stage)
    {
        using var phase=new Phase();using var f=new DeletionRequestFixture(database:new(){OnStep=(s,_,_)=>{if(s==stage)phase.Block();}});Task<(HttpStatusCode Status,JsonElement Json)> post=f.Post();string? id=null;
        try
        {
            if(!stage.StartsWith("create")){var first=await post;id=first.Json.GetProperty("data").GetProperty("deletion_request_id").GetString();await f.Approve();}
            await phase.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            if(stage.StartsWith("create")){var frozen=await post;Assert.Equal(HttpStatusCode.ServiceUnavailable,frozen.Status);Assert.Equal(0,f.F.Ui.Begins);f.F.Clock.Advance(3.1);}
            var busy=await f.Post("another-key");Assert.Equal(HttpStatusCode.ServiceUnavailable,busy.Status);Assert.False(f.Coordinator.WaitForIdleAsync().IsCompleted);
            phase.Release();if(id is not null){await f.Finish(id);Assert.Equal(1,f.F.Native.Calls);}else {Assert.True(await f.Coordinator.WaitForIdleAsync().WaitAsync(TimeSpan.FromSeconds(5)));Assert.Equal(0,f.F.Native.Calls);}
        }
        finally{phase.Release();}
    }
    [Fact]
    public async Task ApprovedPermitExpiresWhileExecutionReservationIsPausedAndNoFreshBudgetOrNativeIsInvented()
    {
        using var phase=new Phase();using var f=new DeletionRequestFixture(database:new(){OnStep=(s,_,_)=>{if(s=="reserve_after_close")phase.Block();}});var id=await f.Create();await f.Approve();
        try{await phase.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));f.F.Clock.Advance(61);phase.Release();await f.Finish(id);Assert.Equal(0,f.F.Native.Calls);Assert.Equal(0,f.Scalar("SELECT count(*) FROM ordinary_deletion_operations;"));}
        finally{phase.Release();}
    }
    [Fact]
    public async Task AcceptedFactSurvivesFailedFinalRequestWriteAndFreshReadsJoinTheSettledLedger()
    {
        using var f=new DeletionRequestFixture(database:new(){Fault="finish_before_open"});var id=await f.Create();await f.Approve();var r=await f.Finish(id);
        Assert.Equal("execution_reserved",r.GetProperty("persisted_request_phase").GetString());Assert.Equal("accepted",r.GetProperty("disposition_progress").GetString());Assert.True(r.GetProperty("file_action_performed").GetBoolean());Assert.Equal("committed",r.GetProperty("ledger_settlement").GetString());Assert.Equal(1,f.F.Native.Calls);
        using var authority=f.F.Service();using var reader=new RetentionDeletionCoordinator(f.F.Db.Store,UnifiedHistoryTestDatabase.Sid,UnifiedHistoryTestDatabase.Session,f.F.Videos,authority,queryAdmission:new());var observed=await reader.ReadAsync(id);Assert.Equal("accepted",observed!.DispositionProgress);Assert.True(observed.FileActionPerformed);Assert.Equal(1,f.F.Ui.Begins);
    }
    [Theory][InlineData("rollback")][InlineData("native")]
    public async Task UnknownOriginalResourcesNeverStartRequestFinalWriter(string fault)
    {
        var guard=new RetentionGuardFixture.TraceDatabase{Fault=fault=="rollback"?fault:null};using var f=new DeletionRequestFixture(guard:guard);
        if(fault=="native")f.F.Files.Api.CloseValue=n=>n.Leaf?AgentRecorder.Infrastructure.OriginalFileCloseOutcome.Unknown:AgentRecorder.Infrastructure.OriginalFileCloseOutcome.Closed;
        var id=await f.Create();await f.Approve();Assert.False(await f.Coordinator.WaitForIdleAsync().WaitAsync(TimeSpan.FromSeconds(7)));Assert.DoesNotContain(f.Database.Calls,x=>x.Stage.StartsWith("finish_"));var r=await f.Read(id);Assert.True(r.GetProperty("file_action_performed").GetBoolean());Assert.False(r.GetProperty("our_resources_retired").GetBoolean());Assert.Equal("not_committed",r.GetProperty("ledger_settlement").GetString());
    }
    [Fact]
    public async Task BoundedQueryTimeoutAndHttpStopKeepActualQueryAdmissionUntilWorkerExit()
    {
        using var phase=new Phase();using var f=new DeletionRequestFixture();var id=await f.Create();await f.Approve();await f.Finish(id);
        var deadline=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);f.Coordinator.DeadlineForTests=_=>deadline.Task;f.Database.OnStep=(s,_,_)=>{if(s=="query_after_open")phase.Block();};
        var read=f.Client.GetAsync(DeletionRequestFixture.Endpoint+"/"+id);
        try
        {
            await phase.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));deadline.TrySetResult();using var timeout=await read;Assert.Equal(HttpStatusCode.ServiceUnavailable,timeout.StatusCode);
            using var busy=await f.Client.GetAsync(DeletionRequestFixture.Endpoint+"/"+id);Assert.Equal(HttpStatusCode.ServiceUnavailable,busy.StatusCode);Assert.False(f.Server.Stop(TimeSpan.FromMilliseconds(50)));phase.Release();Assert.True(await f.Coordinator.WaitForIdleAsync().WaitAsync(TimeSpan.FromSeconds(5)));Assert.Equal(1,f.F.Native.Calls);
        }
        finally{phase.Release();}
    }
    [Fact]
    public async Task HostStopCancelsPendingUiAndWaitsForActualUiRetirement()
    {
        using var f=new DeletionRequestFixture();await f.Create();await f.F.Ui.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));Assert.False(f.Server.Stop(TimeSpan.FromMilliseconds(50)));Assert.Equal(0,f.F.Native.Calls);Assert.False(f.Coordinator.WaitForIdleAsync().IsCompleted);f.F.Ui.Retire.TrySetResult(true);Assert.True(await f.Coordinator.WaitForIdleAsync().WaitAsync(TimeSpan.FromSeconds(5)));Assert.Equal(0,f.F.Native.Calls);
    }
    [Fact]
    public async Task ActualTcpResetAfterDurableCreationDoesNotCancelOwnedUiAndRetryCannotDuplicateIt()
    {
        using var f=new DeletionRequestFixture();using var tcp=new System.Net.Sockets.TcpClient();await tcp.ConnectAsync(IPAddress.Loopback,f.Server.BoundPort);
        var body=JsonSerializer.Serialize(new{recording_id=f.F.Row.RecordingId});var bytes=System.Text.Encoding.UTF8.GetBytes(body);
        var header=$"POST {DeletionRequestFixture.Endpoint} HTTP/1.1\r\nHost: localhost\r\nX-Agent-Recorder-Key: {AgentRecorder.Infrastructure.ApiKeyAuth.CurrentApiKey}\r\nIdempotency-Key: owned-key\r\nContent-Length: {bytes.Length}\r\n\r\n";
        await tcp.GetStream().WriteAsync(System.Text.Encoding.UTF8.GetBytes(header));await tcp.GetStream().WriteAsync(bytes);
        await f.F.Ui.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));Assert.Equal(1,f.Scalar("SELECT count(*) FROM ordinary_deletion_requests;"));
        tcp.Client.LingerState=new(true,0);tcp.Close();await Task.Delay(80);var replay=await f.Post();Assert.Equal(HttpStatusCode.Accepted,replay.Status);
        Assert.Equal(1,f.F.Ui.Begins);Assert.Equal(0,f.F.Native.Calls);await f.Approve();await f.Finish(replay.Json.GetProperty("data").GetProperty("deletion_request_id").GetString()!);Assert.Equal(1,f.F.Native.Calls);
    }
    [Fact]
    public async Task NativeReturnPauseKeepsUnknownFrozenObservationAndBusyUntilActualRetirement()
    {
        using var phase=new Phase();using var f=new DeletionRequestFixture();f.F.Native.AfterRequest=_=>phase.Block();var id=await f.Create();await f.Approve();
        try
        {
            await phase.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));var frozen=await f.Read(id);Assert.Equal("unknown",frozen.GetProperty("disposition_progress").GetString());Assert.Equal(JsonValueKind.Null,frozen.GetProperty("file_action_performed").ValueKind);
            Assert.Equal(HttpStatusCode.ServiceUnavailable,(await f.Post("other-key")).Status);Assert.False(f.Coordinator.WaitForIdleAsync().IsCompleted);
            phase.Release();var final=await f.Finish(id);Assert.Equal("accepted",final.GetProperty("disposition_progress").GetString());Assert.Equal("unknown",frozen.GetProperty("disposition_progress").GetString());Assert.Equal(1,f.F.Native.Calls);
        }
        finally{phase.Release();}
    }
    [Theory][InlineData("prepare_before_commit")][InlineData("prepare_after_commit")][InlineData("settle_before_commit")][InlineData("settle_after_commit")]
    public async Task OriginalLedgerFaultsThroughActualApiNeverEraseAcceptedOrInventMissingOperation(string stage)
    {
        var io=new DeletionLedgerFixture.Io{Fault=stage};using var f=new DeletionRequestFixture(ledger:io);var id=await f.Create();await f.Approve();var final=await f.Finish(id);var prepare=stage.StartsWith("prepare");
        Assert.Equal(prepare?0:1,f.F.Native.Calls);Assert.Equal(1,f.F.Ui.Begins);Assert.Equal("committed",final.GetProperty("request_write_commit").GetString());
        if(stage=="prepare_before_commit"){Assert.Equal(JsonValueKind.Null,final.GetProperty("operation_id").ValueKind);Assert.Equal(JsonValueKind.Null,final.GetProperty("disposition_progress").ValueKind);}
        else Assert.Equal(prepare?"not_attempted":"accepted",final.GetProperty("disposition_progress").GetString());
        if(!prepare)Assert.True(final.GetProperty("file_action_performed").GetBoolean());
        await f.Post();Assert.Equal(prepare?0:1,f.F.Native.Calls);Assert.Equal(1,f.F.Ui.Begins);
    }
    [Fact]
    public async Task UnknownQueryCloseKeepsItsOwnQueryQuotaAndHostShutdownUnretired()
    {
        using var f=new DeletionRequestFixture();var id=await f.Create();await f.Approve();await f.Finish(id);f.Database.UnknownClose="query";
        using var first=await f.Client.GetAsync(DeletionRequestFixture.Endpoint+"/"+id);Assert.Equal(HttpStatusCode.ServiceUnavailable,first.StatusCode);
        using var second=await f.Client.GetAsync(DeletionRequestFixture.Endpoint+"/"+id);Assert.Equal(HttpStatusCode.ServiceUnavailable,second.StatusCode);
        Assert.False(await f.Coordinator.WaitForIdleAsync().WaitAsync(TimeSpan.FromSeconds(5)));Assert.False(f.Server.Stop(TimeSpan.FromMilliseconds(50)));Assert.Equal(1,f.F.Native.Calls);
    }
}
