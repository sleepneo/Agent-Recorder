using AgentRecorder.Infrastructure;
using AgentRecorder.Persistence;
using Xunit;
using Phase=AgentRecorder.Tests.OriginalFileHoldFixture.Phase;

namespace AgentRecorder.Tests;

[Collection("NonParallel-AgentRecorderDataDir")]
public sealed class LocalRetentionDeletionLifecycleTests
{
    [Fact]
    public async Task ConcurrentAndLateRequestsCannotStartSecondDispositionOrOldExecuteCallback()
    {
        using var f=new LocalRetentionDeletionFixture();using var phase=new Phase();f.Native.BeforeRequest=_=>phase.Block();
        using var a=f.Service();var s=a.Request(f.Row.RecordingId);var p=await f.Approve(a,s);var op=a.RequestDeletion(p,f.Row.RecordingId);
        try
        {
            await phase.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var rejected=await Task.WhenAll(Enumerable.Range(0,12).Select(_=>Task.Run(async()=>await a.RequestDeletion(p,f.Row.RecordingId).Response)));
            Assert.All(rejected,r=>Assert.Equal(LocalDeletionProgress.NotAttempted,r.Progress));
            var old=await a.Execute(p,f.Row.RecordingId,_=>throw new InvalidOperationException());Assert.False(old.ApprovalConsumed);Assert.Equal(LocalRetentionState.ExecutionRejected,old.State);
            Assert.Equal(1,f.Native.Calls);phase.Release();await LocalRetentionDeletionFixture.Finish(op,s);
        }
        finally{phase.Release();await op.Retirement.WaitAsync(TimeSpan.FromSeconds(6));}
    }

    [Theory]
    [InlineData("before_db")][InlineData("after_consumed")][InlineData("deadline")][InlineData("permit_expired")]
    public async Task CancellationOrDeadlineWinningBeforeSubmissionHasZeroNativeCalls(string boundary)
    {
        using var f=new LocalRetentionDeletionFixture();using var phase=new Phase();var db=new RetentionGuardFixture.TraceDatabase();
        using var a=f.Service(db);var s=a.Request(f.Row.RecordingId);var p=await f.Approve(a,s);
        if(boundary=="before_db")db.BeforeOpen=phase.Block;
        else f.Files.Api.Before=c=>{if(c.Stage=="basic"&&c.Number==5&&f.Files.Api.Opened.Any(n=>n.Leaf&&n.Index==c.Index))phase.Block();};
        var op=a.RequestDeletion(p,f.Row.RecordingId);
        try
        {
            await phase.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            if(boundary=="deadline")f.Clock.Advance(3.1);else if(boundary=="permit_expired")f.Clock.ShiftUtc(60);else s.Dispose();
            Assert.False(op.Retirement.IsCompleted);f.AssertProtected();phase.Release();var r=await LocalRetentionDeletionFixture.Finish(op,s);
            Assert.Equal(LocalDeletionProgress.NotAttempted,r.Progress);Assert.False(r.FileActionPerformed);Assert.Equal(0,f.Native.Calls);
            Assert.Equal(boundary!="before_db",r.ApprovalConsumed);
        }
        finally{phase.Release();await op.Retirement.WaitAsync(TimeSpan.FromSeconds(6));}
    }

    [Theory]
    [InlineData("call",true)][InlineData("call",false)][InlineData("return",true)][InlineData("return",false)]
    [InlineData("rollback",true)][InlineData("rollback",false)][InlineData("close",true)][InlineData("close",false)]
    public async Task FreezeRetainsLiveLayersAndLateAcceptedFactWithoutRewritingResponse(string phaseName,bool timeout)
    {
        using var f=new LocalRetentionDeletionFixture();using var phase=new Phase();var db=new RetentionGuardFixture.TraceDatabase();
        if(phaseName=="call")f.Native.BeforeRequest=_=>phase.Block();
        if(phaseName=="return")f.Native.AfterRequest=_=>phase.Block();
        if(phaseName=="rollback")db.BeforeRollback=phase.Block;
        if(phaseName=="close")f.Files.Api.CloseValue=n=>{if(n.Leaf)phase.Block();return OriginalFileCloseOutcome.Closed;};
        using var a=f.Service(db);var s=a.Request(f.Row.RecordingId);var p=await f.Approve(a,s);var op=a.RequestDeletion(p,f.Row.RecordingId);
        try
        {
            await phase.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            if(timeout)f.Clock.Advance(3.1);else s.Dispose();
            var frozen=await op.Response.WaitAsync(TimeSpan.FromSeconds(5));var expected=phaseName is "call" or "return"?LocalDeletionProgress.Indeterminate:LocalDeletionProgress.DispositionAccepted;
            Assert.Equal(expected,frozen.Progress);Assert.Equal(phaseName is "call" or "return"?(bool?)null:true,frozen.FileActionPerformed);Assert.Null(frozen.OurResourcesRetired);
            Assert.False(op.Retirement.IsCompleted);Assert.False(s.Retirement.IsCompleted);f.AssertProtected();
            using var other=f.Service();Assert.Equal(LocalRetentionState.Busy,(await other.Request(f.Row.RecordingId).Response).State);
            Assert.Equal(LocalDeletionProgress.NotAttempted,(await a.RequestDeletion(p,f.Row.RecordingId).Response).Progress);Assert.Equal(1,f.Native.Calls);
            if(phaseName!="close")
            {
                using var writer=f.Writer();Assert.Equal(5,Assert.Throws<Microsoft.Data.Sqlite.SqliteException>(()=>RetentionGuardFixture.Write(writer,"UPDATE ordinary_recording_history SET source_title='blocked';")).SqliteErrorCode);
            }
            phase.Release();var settled=await LocalRetentionDeletionFixture.Finish(op,s);
            Assert.Equal(LocalDeletionProgress.DispositionAccepted,settled.Progress);Assert.True(settled.FileActionPerformed);Assert.True(settled.ApprovalConsumed);Assert.Equal(1,f.Native.Calls);
            Assert.Same(frozen,await op.Response);Assert.Equal(expected,frozen.Progress);Assert.NotSame(frozen,settled);
            using var control=f.Writer();RetentionGuardFixture.Write(control,"UPDATE ordinary_recording_history SET source_title='after';");
        }
        finally{phase.Release();await op.Retirement.WaitAsync(TimeSpan.FromSeconds(7));}
    }

    [Theory]
    [InlineData("throw")][InlineData("unknown")][InlineData("invalid")]
    public async Task CallExceptionOrUnknownNeverMeansNoActionAndIsNotRetried(string kind)
    {
        using var f=new LocalRetentionDeletionFixture();f.Native.FakeRequest=()=>kind=="throw"?throw new IOException("PRIVATE_CALL_BOUNDARY"):
            new(kind=="unknown"?OriginalFileDispositionOutcome.Indeterminate:(OriginalFileDispositionOutcome)999);
        using var a=f.Service();var s=a.Request(f.Row.RecordingId);var p=await f.Approve(a,s);var r=await LocalRetentionDeletionFixture.Finish(a.RequestDeletion(p,f.Row.RecordingId),s);
        Assert.Equal(LocalDeletionProgress.Indeterminate,r.Progress);Assert.Null(r.FileActionPerformed);Assert.Equal(LocalDeletionReason.NativeUnknown,r.Reason);Assert.True(r.ApprovalConsumed);
        Assert.Equal(LocalDeletionProgress.NotAttempted,(await a.RequestDeletion(p,f.Row.RecordingId).Response).Progress);Assert.Equal(1,f.Native.Calls);
    }

    [Theory]
    [InlineData("rollback")][InlineData("transaction_dispose")][InlineData("close")][InlineData("connection_dispose")][InlineData("native_failed")][InlineData("native_unknown")]
    public async Task AcceptedFactSurvivesUnknownCleanupWhichKeepsBookingAndBusy(string failure)
    {
        using var f=new LocalRetentionDeletionFixture();var db=new RetentionGuardFixture.TraceDatabase{Fault=failure.StartsWith("native")?null:failure};
        if(failure.StartsWith("native"))f.Files.Api.CloseValue=n=>n.Leaf?(failure=="native_failed"?OriginalFileCloseOutcome.Failed:OriginalFileCloseOutcome.Unknown):OriginalFileCloseOutcome.Closed;
        using var a=f.Service(db);var s=a.Request(f.Row.RecordingId);var p=await f.Approve(a,s);var op=a.RequestDeletion(p,f.Row.RecordingId);
        var r=await op.Retirement.WaitAsync(TimeSpan.FromSeconds(6));Assert.False(r.OurResourcesRetired);Assert.False(await s.Retirement.WaitAsync(TimeSpan.FromSeconds(6)));
        Assert.Equal(LocalDeletionProgress.DispositionAccepted,r.Progress);Assert.True(r.FileActionPerformed);Assert.True(r.ApprovalConsumed);Assert.NotNull(r.Retirement);Assert.Equal(1,f.Native.Calls);f.AssertProtected();
        if(failure.StartsWith("native"))Assert.False(r.Retirement!.Preparation!.Scope!.Retired);else Assert.False(r.Retirement!.Database!.Retired);
        using var other=f.Service();Assert.Equal(LocalRetentionState.Busy,(await other.Request(f.Row.RecordingId).Response).State);
        s.Dispose();Assert.Equal(LocalDeletionProgress.NotAttempted,(await a.RequestDeletion(p,f.Row.RecordingId).Response).Progress);Assert.Equal(1,f.Native.Calls);
    }
}
