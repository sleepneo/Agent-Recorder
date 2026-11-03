using System.Net;
using AgentRecorder.Infrastructure;
using AgentRecorder.Persistence;
using Xunit;
using static AgentRecorder.Tests.BatchDeletion388Fixture;
using static AgentRecorder.Tests.UnifiedHistoryTestDatabase;

namespace AgentRecorder.Tests;
[Collection("NonParallel-AgentRecorderDataDir")]
public sealed class BatchDeletion388RecoveryTests
{
    [Theory][InlineData("batchcreate_before_commit",false)][InlineData("batchcreate_after_commit",true)]
    public async Task ParentCommitBoundaryNeverCreatesAChildOrUiOnUnknownCreation(string stage,bool committed)
    {
        var io=new DeletionRequestFixture.Io {Fault=stage};using var f=new BatchDeletion388Fixture(batchIo:io);
        Assert.Equal(HttpStatusCode.ServiceUnavailable,(await f.Post()).Status);Assert.True(await f.Batch.WaitForIdleAsync().WaitAsync(TimeSpan.FromSeconds(6)));
        Assert.Equal(committed?1:0,f.Scalar("SELECT count(*) FROM ordinary_deletion_batches;"));Assert.Equal(committed?2:0,f.Scalar("SELECT count(*) FROM ordinary_deletion_batch_items;"));Assert.Equal(0,f.Scalar("SELECT count(*) FROM ordinary_deletion_requests;"));Assert.Equal(0,f.Prompts.Begins);Assert.Equal(0,f.Native.Calls);
        io.Fault=null;
        if(committed)
        {
            var replay=await f.Post();Assert.Equal(HttpStatusCode.Accepted,replay.Status);Assert.Equal("interrupted",replay.Json.GetProperty("data").GetProperty("batch_state").GetString());Assert.Equal(0,f.Prompts.Begins);Assert.Equal(0,f.Native.Calls);
        }
    }
    [Theory][InlineData("created")][InlineData("reserved")][InlineData("child_retired")][InlineData("item_committed")]
    public async Task DeterministicOrchestratorCrashThenGetAndSameKeyNeverResume(string checkpoint)
    {
        using var f=new BatchDeletion388Fixture();f.Batch.CheckpointForTests=(stage,n)=>{if(stage==checkpoint)throw new IOException("synthetic crash");};
        var id=await f.Create();
        if(checkpoint is "child_retired" or "item_committed"){var p=await f.Prompts.Next();p.Decide(LocalRetentionUiChoice.Approve);}
        Assert.True(await f.Batch.WaitForIdleAsync().WaitAsync(TimeSpan.FromSeconds(6)));var begins=f.Prompts.Begins;var calls=f.Native.Calls;
        using var restarted=NewLifetime(f);var read=await restarted.ReadAsync(id);Assert.Equal("interrupted",read!.BatchState);
        var replay=await restarted.RequestAsync(f.Ids,"batch-key");Assert.Equal(id,replay.BatchDeletionRequestId);Assert.Equal("interrupted",replay.BatchState);Assert.All(replay.Items.Skip(1),i=>Assert.Equal("not_requested",i.State));
        Assert.Equal(begins,f.Prompts.Begins);Assert.Equal(calls,f.Native.Calls);Assert.Equal(checkpoint is "created" or "reserved"?0:1,calls);
    }
    [Theory][InlineData("batchreserve_before_commit")][InlineData("batchreserve_after_commit")][InlineData("batchitemfinish_before_commit")][InlineData("batchitemfinish_after_commit")][InlineData("batchfinish_before_commit")][InlineData("batchfinish_after_commit")]
    public async Task ParentBindingAndSettlementFaultsStopWithoutReplacingFrozenChildKeys(string stage)
    {
        var io=new DeletionRequestFixture.Io {Fault=stage};using var f=new BatchDeletion388Fixture(batchIo:io);var id=await f.Create();
        if(!stage.StartsWith("batchreserve")){var p=await f.Prompts.Next();p.Decide(LocalRetentionUiChoice.Reject);}
        Assert.True(await f.Batch.WaitForIdleAsync().WaitAsync(TimeSpan.FromSeconds(7)));var before=f.F.Db.Snapshot();var prompts=f.Prompts.Begins;
        io.Fault=null;using var restarted=NewLifetime(f);await restarted.ReadAsync(id);Assert.Equal(id,(await restarted.RequestAsync(f.Ids,"batch-key")).BatchDeletionRequestId);
        Assert.Equal(before,f.F.Db.Snapshot());Assert.Equal(prompts,f.Prompts.Begins);Assert.Equal(0,f.Native.Calls);Assert.InRange(f.Scalar("SELECT count(*) FROM ordinary_deletion_requests;"),0,1);
    }
    [Theory][InlineData("create_before_commit")][InlineData("create_after_commit")][InlineData("reserve_before_commit")][InlineData("reserve_after_commit")][InlineData("finish_before_commit")][InlineData("finish_after_commit")]
    public async Task OriginalSingleRequestCommitFaultsAreBoundToOneFrozenChildAndNeverAdvance(string stage)
    {
        var io=new DeletionRequestFixture.Io {Fault=stage};using var f=new BatchDeletion388Fixture(childIo:io);var id=await f.Create();
        if(!stage.StartsWith("create")){var p=await f.Prompts.Next();p.Decide(LocalRetentionUiChoice.Approve);}
        var result=await f.Finish(id);Assert.Equal("stopped",result.BatchState);Assert.Equal(1,result.NotRequestedCount);Assert.Equal(stage.StartsWith("finish")?1:0,f.Native.Calls);Assert.InRange(f.Prompts.Begins,0,1);
        io.Fault=null;using var restarted=NewLifetime(f);await restarted.RequestAsync(f.Ids,"batch-key");Assert.Equal(stage.StartsWith("finish")?1:0,f.Native.Calls);Assert.InRange(f.Prompts.Begins,0,1);
    }
    [Theory][InlineData("prepare_before_commit")][InlineData("prepare_after_commit")][InlineData("settle_before_commit")][InlineData("settle_after_commit")]
    public async Task OriginalLedgerFaultBeforeOrAfterNativeNeverBecomesABatchPermit(string stage)
    {
        var io=new DeletionLedgerFixture.Io {Fault=stage};using var f=new BatchDeletion388Fixture(ledger:io);var id=await f.Create();var p=await f.Prompts.Next();p.Decide(LocalRetentionUiChoice.Approve);
        var result=await f.Finish(id);Assert.Equal("stopped",result.BatchState);Assert.Equal(1,result.NotRequestedCount);Assert.Equal(stage.StartsWith("settle")?1:0,f.Native.Calls);Assert.Equal(1,f.Prompts.Begins);
        using var restarted=NewLifetime(f);await restarted.ReadAsync(id);await restarted.RequestAsync(f.Ids,"batch-key");Assert.Equal(stage.StartsWith("settle")?1:0,f.Native.Calls);
    }
    [Fact]
    public async Task PendingUiAfterRestartIsOnlyAnInterruptedObservationAndCannotBorrowOldPermit()
    {
        using var f=new BatchDeletion388Fixture();var id=await f.Create();var p=await f.Prompts.Next();
        using var restarted=NewLifetime(f);Assert.Equal("interrupted",(await restarted.ReadAsync(id))!.BatchState);Assert.Equal(id,(await restarted.RequestAsync(f.Ids,"batch-key")).BatchDeletionRequestId);Assert.Equal(1,f.Prompts.Begins);Assert.Equal(0,f.Native.Calls);
        p.Decide(LocalRetentionUiChoice.Reject);await f.Finish(id);
    }
    [Theory][InlineData("native")][InlineData("ui")]
    public async Task HostCancellationStopsRemainingButRetainsAdmissionUntilExactChildRetires(string stage)
    {
        using var barrier=new BatchDeletion388Fixture.Barrier();using var f=new BatchDeletion388Fixture();f.Prompts.AutoRetireCancellation=false;
        if(stage=="native")f.Native.AfterRequest=_=>barrier.Block();var id=await f.Create();var p=await f.Prompts.Next();
        if(stage=="native"){p.Decide(LocalRetentionUiChoice.Approve);await barrier.Entered.Task.WaitAsync(TimeSpan.FromSeconds(6));}
        f.Batch.Dispose();
        try
        {
            Assert.False(f.Batch.WaitForIdleAsync().IsCompleted);Assert.NotNull(f.Batch.HostShutdownBusyReason);Assert.Equal(1,f.Prompts.Begins);
            using var other=NewLifetime(f);await Assert.ThrowsAsync<DeletionRequestUnavailableException>(()=>other.RequestAsync(f.Ids,"new-key"));
        }
        finally{p.Decide(LocalRetentionUiChoice.Closed);barrier.Release();}
        Assert.True(await f.Batch.WaitForIdleAsync().WaitAsync(TimeSpan.FromSeconds(6)));Assert.Equal(1,f.Prompts.Begins);Assert.Equal(stage=="native"?1:0,f.Native.Calls);
        using var reader=NewLifetime(f);Assert.Equal("stopped",(await reader.ReadAsync(id))!.BatchState);
    }
    [Theory][InlineData("batchcreate")][InlineData("batchquery")][InlineData("finish")]
    public async Task UnknownDatabaseCloseRetainsActualAdmissionAndShutdownBusy(string stage)
    {
        var io=new DeletionRequestFixture.Io {UnknownClose=stage=="batchquery"?null:stage};using var f=new BatchDeletion388Fixture(batchIo:stage=="finish"?null:io,childIo:stage=="finish"?io:null);
        var post=await f.Post();
        if(stage!="batchcreate")
        {
            Assert.Equal(HttpStatusCode.Accepted,post.Status);var p=await f.Prompts.Next();p.Decide(LocalRetentionUiChoice.Reject);
            if(stage=="batchquery")
            {
                Assert.True(await f.Batch.WaitForIdleAsync().WaitAsync(TimeSpan.FromSeconds(6)));io.UnknownClose="batchquery";
                await Assert.ThrowsAsync<DeletionRequestUnavailableException>(()=>f.Batch.ReadAsync(post.Json.GetProperty("data").GetProperty("batch_deletion_request_id").GetString()!));
            }
        }
        Assert.False(await f.Batch.WaitForIdleAsync().WaitAsync(TimeSpan.FromSeconds(6)));Assert.NotNull(f.Batch.HostShutdownBusyReason);Assert.False(f.Server.Stop(TimeSpan.FromMilliseconds(100)));Assert.InRange(f.Prompts.Begins,0,1);Assert.Equal(0,f.Native.Calls);
    }
    [Fact]
    public async Task SlowQueryDeadlineKeepsActualConnectionOnItsWorkerUntilRelease()
    {
        using var barrier=new BatchDeletion388Fixture.Barrier();using var f=new BatchDeletion388Fixture();var id=await f.Create();var p=await f.Prompts.Next();p.Decide(LocalRetentionUiChoice.Reject);await f.Finish(id);
        Microsoft.Data.Sqlite.SqliteConnection? connection=null;f.BatchIo.OnStep=(stage,c,t)=>{if(stage=="batchquery_after_begin"){connection=c;barrier.Block();}};
        var deadline=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);f.Batch.DeadlineForTests=c=>deadline.Task.WaitAsync(c);
        var read=f.Batch.ReadAsync(id);await barrier.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));deadline.TrySetResult();
        using var replacement=NewLifetime(f);var replacementRetirement=replacement.WaitForIdleAsync();
        try
        {
            await Assert.ThrowsAsync<DeletionRequestUnavailableException>(()=>read);Assert.Equal(System.Data.ConnectionState.Open,connection!.State);Assert.NotNull(f.Batch.HostShutdownBusyReason);
            await Assert.ThrowsAsync<DeletionRequestUnavailableException>(()=>f.Batch.ReadAsync(id));Assert.False(f.Batch.WaitForIdleAsync().IsCompleted);
            Assert.NotNull(replacement.HostShutdownBusyReason);Assert.False(replacementRetirement.IsCompleted);
        }
        finally{barrier.Release();}
        Assert.True(await f.Batch.WaitForIdleAsync().WaitAsync(TimeSpan.FromSeconds(5)));Assert.Equal(System.Data.ConnectionState.Closed,connection!.State);
        Assert.True(await replacementRetirement.WaitAsync(TimeSpan.FromSeconds(5)));Assert.Null(replacement.HostShutdownBusyReason);
    }
    private static RetentionBatchDeletionCoordinator NewLifetime(BatchDeletion388Fixture f)=>new(f.F.Db.Store,Sid,Session,f.F.Videos,f.Single,f.F.Clock,queries:f.Queries);
    [Theory][InlineData("flow")][InlineData("query")]
    public async Task NewCoordinatorCannotTreatAnOlderOwnersUnknownResourcesAsIdle(string stage)
    {
        var io=new DeletionRequestFixture.Io {UnknownClose=stage=="flow"?"batchcreate":null};using var f=new BatchDeletion388Fixture(batchIo:io);
        var response=await f.Post();
        if(stage=="query")
        {
            var id=response.Json.GetProperty("data").GetProperty("batch_deletion_request_id").GetString()!;var p=await f.Prompts.Next();p.Decide(LocalRetentionUiChoice.Reject);await f.Finish(id);io.UnknownClose="batchquery";
            await Assert.ThrowsAsync<DeletionRequestUnavailableException>(()=>f.Batch.ReadAsync(id));
        }
        Assert.False(await f.Batch.WaitForIdleAsync().WaitAsync(TimeSpan.FromSeconds(5)));
        using var replacement=NewLifetime(f);Assert.NotNull(replacement.HostShutdownBusyReason);Assert.False(await replacement.WaitForIdleAsync().WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.InRange(f.Prompts.Begins,0,1);Assert.Equal(0,f.Native.Calls);
    }
    [Fact]
    public async Task FinalParentCloseUnknownCannotAdvertiseWholeResourceRetirementOrReleaseAdmission()
    {
        var io=new DeletionRequestFixture.Io {UnknownClose="batchfinish"};using var f=new BatchDeletion388Fixture(batchIo:io);var id=await f.Create();
        var p=await f.Prompts.Next();p.Decide(LocalRetentionUiChoice.Reject);Assert.False(await f.Batch.WaitForIdleAsync().WaitAsync(TimeSpan.FromSeconds(5)));
        var final=await f.Batch.ReadAsync(id);Assert.False(final!.BatchResourcesRetired);Assert.Equal("unknown",final.BatchState);Assert.Equal(1,final.NotRequestedCount);Assert.Equal(0,f.Native.Calls);
    }
    [Fact]
    public async Task NegativeUiRetirementNeverReturnsEitherAdmissionOrStartsAnotherPrompt()
    {
        using var f=new BatchDeletion388Fixture();await f.Create();var p=await f.Prompts.Next();p.Decide(LocalRetentionUiChoice.Reject,false);p.Retired.TrySetResult(false);
        Assert.False(await f.Batch.WaitForIdleAsync().WaitAsync(TimeSpan.FromSeconds(5)));Assert.NotNull(f.Batch.HostShutdownBusyReason);Assert.Equal(1,f.Prompts.Begins);Assert.Equal(0,f.Native.Calls);
        await Assert.ThrowsAsync<DeletionRequestUnavailableException>(()=>f.Single.RequestAsync(f.Ids[1],"other-single"));
    }
    [Fact]
    public async Task CreationResponseTimeoutDoesNotReturnActualDatabaseOrHostAdmissionEarly()
    {
        using var barrier=new BatchDeletion388Fixture.Barrier();var io=new DeletionRequestFixture.Io();using var f=new BatchDeletion388Fixture(batchIo:io);
        Microsoft.Data.Sqlite.SqliteConnection? connection=null;io.OnStep=(stage,c,t)=>{if(stage=="batchcreate_after_begin"){connection=c;barrier.Block();}};
        var post=f.Post();await barrier.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        try
        {
            Assert.Equal(HttpStatusCode.ServiceUnavailable,(await post).Status);Assert.Equal(System.Data.ConnectionState.Open,connection!.State);Assert.NotNull(f.Batch.HostShutdownBusyReason);Assert.False(f.Batch.WaitForIdleAsync().IsCompleted);
            Assert.Equal(0,f.Prompts.Begins);Assert.Equal(0,f.Native.Calls);f.F.Clock.Advance(4);
        }
        finally{barrier.Release();}
        Assert.True(await f.Batch.WaitForIdleAsync().WaitAsync(TimeSpan.FromSeconds(5)));Assert.Equal(System.Data.ConnectionState.Closed,connection!.State);Assert.Equal(0,f.Prompts.Begins);
    }
}
