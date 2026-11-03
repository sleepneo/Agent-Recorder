using System.Net;
using System.Text.Json;
using AgentRecorder.Infrastructure;
using AgentRecorder.Persistence;
using Xunit;
using static AgentRecorder.Tests.BatchDeletion388Fixture;

namespace AgentRecorder.Tests;

[Collection("NonParallel-AgentRecorderDataDir")]
public sealed class BatchDeletion388Tests
{
    [Theory][InlineData(2,false)][InlineData(5,false)][InlineData(2,true)][InlineData(5,true)]
    public async Task OrderedSyntheticFilesRequireDistinctLocalDecisionsAndExactlyOneDispositionEach(int count,bool real)
    {
        using var f=new BatchDeletion388Fixture(count,real);var id=await f.Create();
        Assert.Equal(count,f.Scalar("SELECT count(*) FROM ordinary_deletion_batch_items;"));Assert.Equal(1,f.Scalar("SELECT count(*) FROM ordinary_deletion_batches;"));
        for(var n=0;n<count;n++)
        {
            var prompt=await f.Prompts.Next();Assert.Equal(f.Ids[n],prompt.Intent.Facts.RecordingId);Assert.Equal(n,f.Native.Calls);Assert.Equal(n+1,f.Prompts.Begins);
            prompt.Decide(LocalRetentionUiChoice.Approve,false);Assert.Equal(n,f.Native.Calls);Assert.False(f.Batch.WaitForIdleAsync().IsCompleted);
            prompt.Retired.TrySetResult(true);
        }
        var final=await f.Finish(id);Assert.Equal("dispositions_accepted",final.BatchState);Assert.Equal(count,final.DispositionAcceptedCount);Assert.Equal(0,final.NotRequestedCount);
        Assert.Equal(f.Ids,final.Items.Select(i=>i.RecordingId));Assert.Equal(count,f.Native.Calls);Assert.Equal(count,f.Prompts.Begins);Assert.Equal(count,f.Native.Handles.Distinct().Count());
        Assert.All(final.Items,i=>{Assert.True(i.ResourcesRetired);Assert.Equal("accepted",i.Request!.DispositionProgress);Assert.Equal("committed",i.Request.LedgerSettlement);});
        if(real)foreach(var r in f.Rows)Assert.False(File.Exists(r.OutputPath));else f.PreserveBytes();
        var replay=await f.Post();Assert.Equal(HttpStatusCode.Accepted,replay.Status);Assert.Equal(id,replay.Json.GetProperty("data").GetProperty("batch_deletion_request_id").GetString());Assert.Equal(count,f.Native.Calls);Assert.Equal(count,f.Prompts.Begins);
    }

    [Theory][InlineData("ui")][InlineData("native")][InlineData("child_close")]
    public async Task PublicChildCompletionNeverDispatchesNextUntilActualRetirement(string stage)
    {
        using var barrier=new BatchDeletion388Fixture.Barrier();using var f=new BatchDeletion388Fixture();
        if(stage=="native")f.Native.AfterRequest=_=>{if(f.Native.Calls==1)barrier.Block();};
        if(stage=="child_close")f.ChildIo.OnStep=(s,c,t)=>{if(s=="finish_before_close")barrier.Block();};
        var id=await f.Create();var first=await f.Prompts.Next();first.Decide(LocalRetentionUiChoice.Approve,stage!="ui");
        if(stage!="ui")await barrier.Entered.Task.WaitAsync(TimeSpan.FromSeconds(6));
        try
        {
            Assert.Equal(1,f.Prompts.Begins);Assert.False(f.Batch.WaitForIdleAsync().IsCompleted);Assert.NotNull(f.Batch.HostShutdownBusyReason);
            Assert.Equal(HttpStatusCode.ServiceUnavailable,(await f.Post("other-key")).Status);
            await Assert.ThrowsAsync<DeletionRequestUnavailableException>(()=>f.Single.RequestAsync(f.Ids[1],"single-conflict"));
            using var shutdown=await f.Client.PostAsync("/api/v1/host/shutdown",null);Assert.Equal(HttpStatusCode.Conflict,shutdown.StatusCode);
            var facts=await f.Batch.ReadAsync(id);Assert.Equal("not_requested",facts!.Items[1].State);
        }
        finally{first.Retired.TrySetResult(true);barrier.Release();}
        var second=await f.Prompts.Next();second.Decide(LocalRetentionUiChoice.Reject);var result=await f.Finish(id);
        Assert.Equal(1,result.DispositionAcceptedCount);Assert.Equal(1,f.Native.Calls);Assert.Equal("stopped",result.BatchState);
    }

    [Theory][InlineData("Reject")][InlineData("Closed")][InlineData("Escape")][InlineData("Unavailable")]
    public async Task FirstNonApprovalStopsAllRemainingWithoutNativeOrRollback(string decision)
    {
        using var f=new BatchDeletion388Fixture(5);var id=await f.Create();var p=await f.Prompts.Next();p.Decide(Enum.Parse<LocalRetentionUiChoice>(decision));var result=await f.Finish(id);
        Assert.Equal("stopped",result.BatchState);Assert.Equal(4,result.NotRequestedCount);Assert.Equal(1,f.Prompts.Begins);Assert.Equal(0,f.Native.Calls);f.PreserveBytes();
        Assert.All(result.Items.Skip(1),i=>{Assert.Equal("not_requested",i.State);Assert.Null(i.DeletionRequestId);Assert.Null(i.Request);});
    }

    [Fact]
    public async Task ExpiredLocalDecisionAndBatchLifetimeStopWithoutInventingRetirement()
    {
        using var f=new BatchDeletion388Fixture();f.Prompts.AutoRetireCancellation=false;var id=await f.Create();var p=await f.Prompts.Next();
        f.F.Clock.Advance(601);p.Decide(LocalRetentionUiChoice.Approve,false);
        Assert.False(f.Batch.WaitForIdleAsync().IsCompleted);Assert.Equal(0,f.Native.Calls);Assert.NotNull(f.Batch.HostShutdownBusyReason);
        p.Retired.TrySetResult(true);var final=await f.Finish(id);Assert.Equal("stopped",final.BatchState);Assert.Equal(1,final.NotRequestedCount);Assert.Equal(0,f.Native.Calls);
    }

    [Fact]
    public async Task ConcurrentSameKeyFreezesOneListAndChangedOrderConflicts()
    {
        using var f=new BatchDeletion388Fixture();var requests=await Task.WhenAll(Enumerable.Range(0,5).Select(_=>f.Post()));
        Assert.All(requests,r=>Assert.Equal(HttpStatusCode.Accepted,r.Status));var ids=requests.Select(r=>r.Json.GetProperty("data").GetProperty("batch_deletion_request_id").GetString()).Distinct().ToArray();Assert.Single(ids);
        var changed=await f.Post(body:JsonSerializer.Serialize(new{recording_ids=f.Ids.Reverse()}));Assert.Equal(HttpStatusCode.Conflict,changed.Status);
        var p=await f.Prompts.Next();p.Decide(LocalRetentionUiChoice.Reject);await f.Finish(ids[0]!);Assert.Equal(1,f.Prompts.Begins);Assert.Equal(1,f.Scalar("SELECT count(*) FROM ordinary_deletion_batches;"));Assert.Equal(1,f.Scalar("SELECT count(*) FROM ordinary_deletion_requests;"));
    }

    [Theory][InlineData("bundle")][InlineData("shared")][InlineData("directory")][InlineData("replacement")][InlineData("file_missing")][InlineData("file_denied")][InlineData("native_rejected")][InlineData("native_unknown")]
    public async Task ExistingSafetyGuardsStopRemainingAndKeepPriorAcceptedFacts(string fault)
    {
        using var f=new BatchDeletion388Fixture(3);var id=await f.Create();var first=await f.Prompts.Next();
        // Mutate only this owned fixture after the first UI, before the second
        // candidate's facts/guard. No earlier evaluation acts as authorization.
        var row=f.Rows[1];
        if(fault=="bundle")f.F.Db.Corrupt("UPDATE ordinary_recording_bundles SET status_code='ready',operation_id='aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa',bundle_path='"+row.OutputPath[..^4].Replace("'","''")+".bundle' WHERE recording_id='r1';");
        if(fault=="directory")RetentionBundleNamespace386Tests.Insert(f.F.Db,"ordinary_bundle_contents",row.OutputPath,row.PublicationReceipt!.Anchor);
        if(fault=="replacement")f.F.Files.Api.IdentityValue=(node,n,value)=>node.Leaf&&node.Path==row.OutputPath?value with {IdLow=value.IdLow+100}:value;
        if(fault=="file_denied")f.F.Files.Api.Fail=call=>call.Stage=="open"&&f.F.Files.Api.Parameters.Last().Path==@"\\?\"+row.OutputPath;
        if(fault is "native_rejected" or "native_unknown")f.Native.FakeRequest=()=>f.Native.Calls==1?new(OriginalFileDispositionOutcome.Accepted):new(fault=="native_rejected"?OriginalFileDispositionOutcome.Rejected:OriginalFileDispositionOutcome.Indeterminate,5);
        if(fault=="file_missing")File.Delete(row.OutputPath);
        if(fault=="shared")
        {
            var other=f.F.Db.Recording("shared_foreign",completed:true) with {OutputPath=row.OutputPath,BundleRegistration=new(null)};f.F.Db.Ordinary.Register(other);
        }
        first.Decide(LocalRetentionUiChoice.Approve);
        if(fault is "shared" or "directory" or "replacement" or "file_denied" or "native_rejected" or "native_unknown")
        {var second=await f.Prompts.Next();second.Decide(LocalRetentionUiChoice.Approve);}
        var final=await f.Finish(id);Assert.Equal("stopped",final.BatchState);Assert.Equal(1,final.DispositionAcceptedCount);Assert.Equal(1,final.NotRequestedCount);
        Assert.Equal(fault is "native_rejected" or "native_unknown"?2:1,f.Native.Calls);Assert.Equal("disposition_accepted",final.Items[0].State);Assert.Equal("not_requested",final.Items[2].State);
    }
    [Fact]
    public async Task IndividualSixtySecondTimeoutStopsWithoutStartingTheNextConfirmation()
    {
        using var f=new BatchDeletion388Fixture();var id=await f.Create();var p=await f.Prompts.Next();f.F.Clock.Advance(61);p.Decide(LocalRetentionUiChoice.Approve);
        var final=await f.Finish(id);Assert.Equal("stopped",final.BatchState);Assert.Equal(1,final.NotRequestedCount);Assert.Equal(0,f.Native.Calls);Assert.Equal(1,f.Prompts.Begins);
    }
    [Fact]
    public async Task BackwardsWallClockCannotExtendTheTenMinuteBatchDispatchLifetime()
    {
        var io=new DeletionRequestFixture.Io();using var f=new BatchDeletion388Fixture(batchIo:io);
        io.OnStep=(stage,c,t)=>{if(stage=="batchcreate_after_close")f.F.Clock.ShiftUtc(-600);};
        f.Batch.CheckpointForTests=(stage,n)=>{if(stage=="created")f.F.Clock.ShiftUtc(600);};
        var id=await f.Create();var p=await f.Prompts.Next();f.F.Clock.Advance(601);p.Decide(LocalRetentionUiChoice.Approve);
        var final=await f.Finish(id);Assert.Equal("batch_lifetime_expired",final.StopReason);Assert.Equal(1,final.NotRequestedCount);Assert.Equal(0,f.Native.Calls);Assert.Equal(1,f.Prompts.Begins);
    }
    [Fact]
    public async Task DatabaseUnavailableOnSecondGuardKeepsFirstDispositionAndStopsThird()
    {
        var guard=new RetentionGuardFixture.TraceDatabase();using var f=new BatchDeletion388Fixture(3,guard:guard);var id=await f.Create();var first=await f.Prompts.Next();first.Decide(LocalRetentionUiChoice.Approve);
        var second=await f.Prompts.Next();guard.Fault="open";second.Decide(LocalRetentionUiChoice.Approve);var final=await f.Finish(id);
        Assert.Equal(1,f.Native.Calls);Assert.Equal(2,f.Prompts.Begins);Assert.Equal(1,final.DispositionAcceptedCount);Assert.Equal("not_requested",final.Items[2].State);
    }
    [Fact]
    public async Task AnAlreadyOccupiedOriginalApprovalStopsBatchRatherThanQueueingAnotherPermit()
    {
        using var f=new BatchDeletion388Fixture();using var manual=f.Authority.Request(f.Ids[0]);var p=await f.Prompts.Next();
        try
        {
            var id=await f.Create();var final=await f.Finish(id);Assert.Equal("stopped",final.BatchState);Assert.Equal("child_busy",final.StopReason);Assert.Equal("child_busy",final.Items[0].ReasonCode);Assert.Equal(1,final.NotRequestedCount);Assert.Equal(1,f.Prompts.Begins);Assert.Equal(0,f.Native.Calls);
        }
        finally{p.Decide(LocalRetentionUiChoice.Reject);Assert.True(await manual.Retirement.WaitAsync(TimeSpan.FromSeconds(5)));}
    }
}
