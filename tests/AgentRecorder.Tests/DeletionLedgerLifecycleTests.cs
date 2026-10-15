using AgentRecorder.Persistence;
using Xunit;
using Phase=AgentRecorder.Tests.OriginalFileHoldFixture.Phase;

namespace AgentRecorder.Tests;

[Collection("NonParallel-AgentRecorderDataDir")]
public sealed class DeletionLedgerLifecycleTests
{
    [Fact]
    public async Task MissingRootRetirementProofCannotSettleOrReleaseLocalAdmission()
    {
        using var f=new LocalRetentionDeletionFixture();var clock=new FaultClock(f.Clock);
        var io=new DeletionLedgerFixture.Io{OnStep=(stage,_,_)=>{if(stage=="prepare_after_close")Interlocked.Exchange(ref clock.ThrowUtc,1);}};
        using var a=f.Service(ledgerDatabase:io,clock:clock);var s=a.Request(f.Row.RecordingId);var p=await f.Approve(a,s);
        var r=await a.RequestDeletion(p,f.Row.RecordingId).Retirement.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.False(r.OurResourcesRetired);Assert.True(r.LedgerResourcesRetired);Assert.Null(r.Retirement);
        Assert.Equal(DeletionLedgerCommit.Committed,r.Preparation);Assert.Equal(DeletionLedgerCommit.NotCommitted,r.Settlement);
        Assert.Equal(0,f.Native.Calls);Assert.Empty(f.Files.Api.Opened);Assert.DoesNotContain(io.Calls,x=>x.Stage.StartsWith("settle_"));
        Assert.False(await s.Retirement.WaitAsync(TimeSpan.FromSeconds(5)));
        using var other=f.Service();Assert.Equal(LocalRetentionState.Busy,(await other.Request(f.Row.RecordingId).Response).State);
        Assert.Equal(DeletionRecoveryState.PendingLive,Assert.Single(a.ReadDeletionOperations()).State);
    }
    [Fact]
    public async Task OperationTimerCleanupFailurePreservesAcceptedButFreezesActualRetirementAsUnknown()
    {
        using var f=new LocalRetentionDeletionFixture();var clock=new FaultClock(f.Clock);
        var io=new DeletionLedgerFixture.Io{OnStep=(stage,_,_)=>{if(stage=="settle_after_close")Interlocked.Exchange(ref clock.ThrowDispose,1);}};
        using var a=f.Service(ledgerDatabase:io,clock:clock);var s=a.Request(f.Row.RecordingId);var p=await f.Approve(a,s);
        var r=await a.RequestDeletion(p,f.Row.RecordingId).Retirement.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.False(r.OurResourcesRetired);Assert.False(r.LedgerResourcesRetired);Assert.True(r.Retirement!.Retired);
        Assert.True(r.FileActionPerformed);Assert.Equal(LocalDeletionProgress.DispositionAccepted,r.Progress);Assert.Equal(DeletionLedgerCommit.Committed,r.Settlement);
        Assert.False(await s.Retirement.WaitAsync(TimeSpan.FromSeconds(5)));Assert.Equal(1,f.Native.Calls);
        using var other=f.Service();Assert.Equal(LocalRetentionState.Busy,(await other.Request(f.Row.RecordingId).Response).State);
        var row=Assert.Single(a.ReadDeletionOperations());Assert.Equal(DeletionRecoveryState.Settled,row.State);Assert.True(row.OurResourcesRetired);
    }
    private sealed class FaultClock(TimeProvider inner):TimeProvider
    {
        internal int ThrowUtc,ThrowDispose;
        public override long TimestampFrequency=>inner.TimestampFrequency;
        public override long GetTimestamp()=>inner.GetTimestamp();
        public override DateTimeOffset GetUtcNow()
        {if(Interlocked.Exchange(ref ThrowUtc,0)==1)throw new IOException("private-root-clock");return inner.GetUtcNow();}
        public override ITimer CreateTimer(TimerCallback callback,object? state,TimeSpan dueTime,TimeSpan period)
            =>new CleanupTimer(this,inner.CreateTimer(callback,state,dueTime,period));
        private sealed class CleanupTimer(FaultClock owner,ITimer timer):ITimer
        {
            public bool Change(TimeSpan dueTime,TimeSpan period)=>timer.Change(dueTime,period);
            public void Dispose(){timer.Dispose();if(Interlocked.Exchange(ref owner.ThrowDispose,0)==1)throw new IOException("private-operation-timer-cleanup");}
            public ValueTask DisposeAsync(){Dispose();return ValueTask.CompletedTask;}
        }
    }
    [Fact]
    public async Task PreparationTimeAndRootUseOneStartRatherThanTwoThreeSecondBudgets()
    {
        using var f=new LocalRetentionDeletionFixture();using var phase=new Phase();var io=new DeletionLedgerFixture.Io{OnStep=(name,_,_)=>{if(name=="prepare_after_close")f.Clock.Advance(2);}};
        var db=new RetentionGuardFixture.TraceDatabase{BeforeOpen=phase.Block};using var a=f.Service(database:db,ledgerDatabase:io);var s=a.Request(f.Row.RecordingId);var p=await f.Approve(a,s);var op=a.RequestDeletion(p,f.Row.RecordingId);
        try
        {
            await phase.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));Assert.NotEmpty(f.Files.Api.Opened);f.Clock.Advance(1.1);await op.Response.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.False(op.Retirement.IsCompleted);phase.Release();var r=await LocalRetentionDeletionFixture.Finish(op,s);Assert.Equal(0,f.Native.Calls);Assert.False(r.ApprovalConsumed);Assert.Equal(DeletionLedgerCommit.Committed,r.Preparation);
        }
        finally{phase.Release();await op.Retirement.WaitAsync(TimeSpan.FromSeconds(6));}
    }
    [Fact]
    public async Task LateSuccessfulSettlementCommitStaysCommittedAndDoesNotRewriteFrozenResponse()
    {
        using var f=new LocalRetentionDeletionFixture();using var phase=new Phase();var io=new DeletionLedgerFixture.Io{OnStep=(name,_,_)=>{if(name=="settle_after_commit")phase.Block();}};
        using var a=f.Service(ledgerDatabase:io);var s=a.Request(f.Row.RecordingId);var p=await f.Approve(a,s);var op=a.RequestDeletion(p,f.Row.RecordingId);
        try
        {
            await phase.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));var frozen=await op.Response.WaitAsync(TimeSpan.FromSeconds(5));f.Clock.Advance(3.1);
            var persisted=a.ReadDeletionOperations().Single();Assert.Equal(DeletionRecoveryState.Settled,persisted.State);Assert.Equal(LocalDeletionProgress.DispositionAccepted,persisted.Progress);
            using var other=f.Service();Assert.Equal(LocalRetentionState.Busy,(await other.Request(f.Row.RecordingId).Response).State);Assert.False(s.Retirement.IsCompleted);
            phase.Release();var r=await LocalRetentionDeletionFixture.Finish(op,s);Assert.Equal(DeletionLedgerCommit.Committed,r.Settlement);Assert.Equal(LocalDeletionProgress.DispositionAccepted,r.Progress);Assert.Same(frozen,await op.Response);
        }
        finally{phase.Release();await op.Retirement.WaitAsync(TimeSpan.FromSeconds(6));}
    }
    [Fact]
    public async Task SettlementUnknownCommitRetainsAcceptedAndReaderReportsOnlyActuallyCommittedResult()
    {
        using var f=new LocalRetentionDeletionFixture();var io=new DeletionLedgerFixture.Io();io.OnStep=(stage,_,_)=>{if(stage=="settle_before_commit")io.CommitThenThrow=true;};
        using var a=f.Service(ledgerDatabase:io);var s=a.Request(f.Row.RecordingId);var p=await f.Approve(a,s);var r=await LocalRetentionDeletionFixture.Finish(a.RequestDeletion(p,f.Row.RecordingId),s);
        Assert.Equal(DeletionLedgerCommit.Unknown,r.Settlement);Assert.Equal(LocalDeletionProgress.DispositionAccepted,r.Progress);Assert.True(r.FileActionPerformed);Assert.Equal(1,f.Native.Calls);
        var actual=DeletionLedgerFixture.Reader(f).Read(r.AttemptId)!;Assert.Equal(DeletionRecoveryState.Settled,actual.State);Assert.Equal(LocalDeletionProgress.DispositionAccepted,actual.Progress);
    }
    [Theory]
    [InlineData("settle_before_close")][InlineData("settle_after_close")]
    public async Task UnknownSettlementCloseKeepsAdmissionAndAcceptedButSeparatesLedgerFromOriginalResources(string fault)
    {
        using var f=new LocalRetentionDeletionFixture();var io=new DeletionLedgerFixture.Io{Fault=fault};using var a=f.Service(ledgerDatabase:io);var s=a.Request(f.Row.RecordingId);var p=await f.Approve(a,s);
        var r=await a.RequestDeletion(p,f.Row.RecordingId).Retirement.WaitAsync(TimeSpan.FromSeconds(5));Assert.Equal(LocalDeletionProgress.DispositionAccepted,r.Progress);Assert.True(r.Retirement!.Retired);Assert.False(r.LedgerResourcesRetired);Assert.False(r.OurResourcesRetired);Assert.False(await s.Retirement);
        using var other=f.Service();Assert.Equal(LocalRetentionState.Busy,(await other.Request(f.Row.RecordingId).Response).State);Assert.Equal(1,f.Native.Calls);
    }
    [Theory]
    [InlineData("prepare_before_open")][InlineData("prepare_after_open")][InlineData("prepare_before_begin")][InlineData("prepare_after_begin")]
    [InlineData("prepare_before_insert")][InlineData("prepare_after_insert")][InlineData("prepare_before_commit")][InlineData("prepare_after_commit")]
    public async Task PreparationFailureNeverConsumesOrSubmitsAndDoesNotRetry(string stage)
    {
        using var f=new LocalRetentionDeletionFixture();var io=new DeletionLedgerFixture.Io{Fault=stage};using var a=f.Service(ledgerDatabase:io);var s=a.Request(f.Row.RecordingId);var p=await f.Approve(a,s);
        var r=await LocalRetentionDeletionFixture.Finish(a.RequestDeletion(p,f.Row.RecordingId),s);Assert.False(r.ApprovalConsumed);Assert.Equal(0,f.Native.Calls);Assert.Equal(LocalDeletionProgress.NotAttempted,r.Progress);
        Assert.InRange(io.Commits,0,2);Assert.Equal(LocalDeletionProgress.NotAttempted,(await a.RequestDeletion(p,f.Row.RecordingId).Response).Progress);
    }
    [Theory]
    [InlineData("prepare_before_close")][InlineData("prepare_after_close")][InlineData("transaction")][InlineData("connection")]
    public async Task UnknownPreparationRetirementKeepsHostBusyWithZeroNative(string fault)
    {
        using var f=new LocalRetentionDeletionFixture();var io=new DeletionLedgerFixture.Io{Fault=fault.StartsWith("prepare")?fault:null,DisposeFault=fault.StartsWith("prepare")?null:fault};
        using var a=f.Service(ledgerDatabase:io);var s=a.Request(f.Row.RecordingId);var p=await f.Approve(a,s);var op=a.RequestDeletion(p,f.Row.RecordingId);
        var r=await op.Retirement.WaitAsync(TimeSpan.FromSeconds(5));Assert.False(r.OurResourcesRetired);Assert.False(await s.Retirement.WaitAsync(TimeSpan.FromSeconds(5)));Assert.Equal(0,f.Native.Calls);
        using var other=f.Service();Assert.Equal(LocalRetentionState.Busy,(await other.Request(f.Row.RecordingId).Response).State);Assert.Single(DeletionLedgerFixture.Reader(f).Page());
    }
    [Fact]
    public async Task CommitThenThrowLeavesPreparedUnknownAndCannotCreateProofOrNativeCall()
    {
        using var f=new LocalRetentionDeletionFixture();var io=new DeletionLedgerFixture.Io{CommitThenThrow=true};using var a=f.Service(ledgerDatabase:io);var s=a.Request(f.Row.RecordingId);var p=await f.Approve(a,s);
        var r=await LocalRetentionDeletionFixture.Finish(a.RequestDeletion(p,f.Row.RecordingId),s);Assert.Equal(DeletionLedgerCommit.Unknown,r.Preparation);Assert.Equal(0,f.Native.Calls);
        var row=Assert.Single(DeletionLedgerFixture.Reader(f).Page());Assert.Equal(DeletionRecoveryState.UnsettledUnknown,row.State);Assert.Null(row.Progress);Assert.Equal(1,io.Commits);
    }
    [Theory]
    [InlineData("prepare_before_commit",true)][InlineData("prepare_before_commit",false)]
    [InlineData("prepare_after_commit",true)][InlineData("prepare_after_commit",false)]
    [InlineData("prepare_before_close",true)][InlineData("prepare_before_close",false)]
    public async Task LatePreparationRetainsAdmissionUntilWorkerExitAndCannotStartRoot(string stage,bool timeout)
    {
        using var f=new LocalRetentionDeletionFixture();using var phase=new Phase();var io=new DeletionLedgerFixture.Io{OnStep=(name,_,_)=>{if(name==stage)phase.Block();}};
        using var a=f.Service(ledgerDatabase:io);var s=a.Request(f.Row.RecordingId);var p=await f.Approve(a,s);var op=a.RequestDeletion(p,f.Row.RecordingId);
        try
        {
            await phase.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));if(timeout)f.Clock.Advance(3.1);else s.Dispose();var frozen=await op.Response.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(LocalDeletionProgress.NotAttempted,frozen.Progress);Assert.False(op.Retirement.IsCompleted);Assert.False(s.Retirement.IsCompleted);
            using var other=f.Service();Assert.Equal(LocalRetentionState.Busy,(await other.Request(f.Row.RecordingId).Response).State);Assert.Empty(f.Files.Api.Opened);
            phase.Release();var final=await LocalRetentionDeletionFixture.Finish(op,s);Assert.Equal(0,f.Native.Calls);Assert.False(final.ApprovalConsumed);Assert.Same(frozen,await op.Response);
        }
        finally{phase.Release();await op.Retirement.WaitAsync(TimeSpan.FromSeconds(6));}
    }
    [Theory]
    [InlineData("settle_before_open")][InlineData("settle_after_open")][InlineData("settle_before_begin")][InlineData("settle_after_begin")]
    [InlineData("settle_before_update")][InlineData("settle_after_update")][InlineData("settle_before_commit")][InlineData("settle_after_commit")]
    public async Task SettlementFailureNeverErasesAcceptedNativeFactOrRetries(string stage)
    {
        using var f=new LocalRetentionDeletionFixture();var io=new DeletionLedgerFixture.Io{Fault=stage};using var a=f.Service(ledgerDatabase:io);var s=a.Request(f.Row.RecordingId);var p=await f.Approve(a,s);
        var r=await LocalRetentionDeletionFixture.Finish(a.RequestDeletion(p,f.Row.RecordingId),s);Assert.Equal(LocalDeletionProgress.DispositionAccepted,r.Progress);Assert.True(r.FileActionPerformed);Assert.Equal(1,f.Native.Calls);
        var row=DeletionLedgerFixture.Reader(f).Read(r.AttemptId)!;Assert.Equal(stage=="settle_after_commit"?DeletionRecoveryState.Settled:DeletionRecoveryState.UnsettledUnknown,row.State);
        Assert.Equal(LocalDeletionProgress.NotAttempted,(await a.RequestDeletion(p,f.Row.RecordingId).Response).Progress);
    }
    [Theory]
    [InlineData("rollback")][InlineData("native")]
    public async Task UnknownOriginalGuardOrNativeCleanupNeverOpensSecondSettlementWriter(string fault)
    {
        using var f=new LocalRetentionDeletionFixture();var db=new RetentionGuardFixture.TraceDatabase{Fault=fault=="rollback"?fault:null};var io=new DeletionLedgerFixture.Io();
        if(fault=="native")f.Files.Api.CloseValue=n=>n.Leaf?AgentRecorder.Infrastructure.OriginalFileCloseOutcome.Unknown:AgentRecorder.Infrastructure.OriginalFileCloseOutcome.Closed;
        using var a=f.Service(database:db,ledgerDatabase:io);var s=a.Request(f.Row.RecordingId);var p=await f.Approve(a,s);var r=await a.RequestDeletion(p,f.Row.RecordingId).Retirement.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.False(r.OurResourcesRetired);Assert.Equal(LocalDeletionProgress.DispositionAccepted,r.Progress);Assert.DoesNotContain(io.Calls,x=>x.Stage.StartsWith("settle_"));Assert.False(await s.Retirement);
    }
    [Fact]
    public async Task SettlementPauseKeepsLocalHostBusyAndLateCommitDoesNotRewriteResponse()
    {
        using var f=new LocalRetentionDeletionFixture();using var phase=new Phase();var io=new DeletionLedgerFixture.Io{OnStep=(name,_,_)=>{if(name=="settle_before_commit")phase.Block();}};
        using var a=f.Service(ledgerDatabase:io);var s=a.Request(f.Row.RecordingId);var p=await f.Approve(a,s);var op=a.RequestDeletion(p,f.Row.RecordingId);
        try
        {
            await phase.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));var frozen=await op.Response.WaitAsync(TimeSpan.FromSeconds(5));f.Clock.Advance(3.1);
            Assert.Equal(LocalDeletionProgress.DispositionAccepted,frozen.Progress);Assert.False(op.Retirement.IsCompleted);Assert.False(s.Retirement.IsCompleted);
            using var other=f.Service();Assert.Equal(LocalRetentionState.Busy,(await other.Request(f.Row.RecordingId).Response).State);
            phase.Release();var final=await LocalRetentionDeletionFixture.Finish(op,s);Assert.Equal(1,f.Native.Calls);Assert.Same(frozen,await op.Response);Assert.Equal(DeletionLedgerCommit.NotCommitted,final.Settlement);
        }
        finally{phase.Release();await op.Retirement.WaitAsync(TimeSpan.FromSeconds(6));}
    }
}
