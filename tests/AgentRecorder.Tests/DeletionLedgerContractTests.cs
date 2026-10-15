using AgentRecorder.Persistence;
using AgentRecorder.Infrastructure;
using Xunit;

namespace AgentRecorder.Tests;

[Collection("NonParallel-AgentRecorderDataDir")]
public sealed class DeletionLedgerContractTests
{
    [Fact]
    public async Task OldUnsettledAuditIsNotAUseReferenceAndOnlyFreshLocalApprovalCanTryAgain()
    {
        using var f=new LocalRetentionDeletionFixture();var io=new DeletionLedgerFixture.Io{CommitThenThrow=true};using var first=f.Service(ledgerDatabase:io);var s=first.Request(f.Row.RecordingId);var p=await f.Approve(first,s);
        var old=await LocalRetentionDeletionFixture.Finish(first.RequestDeletion(p,f.Row.RecordingId),s);Assert.Equal(0,f.Native.Calls);
        var ui=new LocalRetentionApprovalFixture.Ui();using var fresh=f.Service(ui:ui);var next=fresh.Request(f.Row.RecordingId);await ui.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));ui.Decide(LocalRetentionUiChoice.Approve);ui.Retire.TrySetResult(true);
        var permit=(await next.Response).Permit!;Assert.NotEqual(p.ApprovalId,permit.ApprovalId);var r=await LocalRetentionDeletionFixture.Finish(fresh.RequestDeletion(permit,f.Row.RecordingId),next);
        Assert.Equal(1,f.Native.Calls);Assert.Equal(DeletionLedgerCommit.Committed,r.Settlement);Assert.Equal(DeletionRecoveryState.UnsettledUnknown,fresh.ReadDeletionOperation(old.AttemptId)!.State);Assert.Equal(2,fresh.ReadDeletionOperations().Count);
    }
    [Fact]
    public async Task AnotherAuthorityReadingLiveAttemptCannotRecoverUnlockOrSubmitIt()
    {
        using var f=new LocalRetentionDeletionFixture();using var phase=new OriginalFileHoldFixture.Phase();f.Native.BeforeRequest=_=>phase.Block();using var a=f.Service();var s=a.Request(f.Row.RecordingId);var p=await f.Approve(a,s);var op=a.RequestDeletion(p,f.Row.RecordingId);
        try
        {
            await phase.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));using var other=f.Service();var row=Assert.Single(other.ReadDeletionOperations());Assert.Equal(DeletionRecoveryState.PendingLive,row.State);
            Assert.Equal(LocalRetentionState.Busy,(await other.Request(f.Row.RecordingId).Response).State);Assert.Equal(LocalDeletionProgress.NotAttempted,(await other.RequestDeletion(null,row.Binding.RecordingId).Response).Progress);Assert.Equal(1,f.Native.Calls);
            phase.Release();await LocalRetentionDeletionFixture.Finish(op,s);
        }
        finally{phase.Release();await op.Retirement.WaitAsync(TimeSpan.FromSeconds(6));}
    }
    [Theory]
    [InlineData("UPDATE ordinary_deletion_operations SET volume_id=zeroblob(7);")]
    [InlineData("UPDATE ordinary_deletion_operations SET file_id=zeroblob(15);")]
    [InlineData("UPDATE ordinary_deletion_operations SET output_path=output_path||char(0)||'hidden';")]
    [InlineData("UPDATE ordinary_deletion_operations SET resources_retired=2;")]
    [InlineData("UPDATE ordinary_deletion_operations SET primary_reason=12;")]
    [InlineData("UPDATE ordinary_deletion_operations SET progress=2,reason=4;")]
    public async Task ReadonlyRecoveryRejectsMalformedPersistentFactsWithoutAnyMediaAction(string sql)
    {
        using var f=new LocalRetentionDeletionFixture();using var a=f.Service();var s=a.Request(f.Row.RecordingId);var p=await f.Approve(a,s);var r=await LocalRetentionDeletionFixture.Finish(a.RequestDeletion(p,f.Row.RecordingId),s);
        f.Db.Corrupt(sql);var before=f.Native.Calls;Assert.ThrowsAny<Exception>(()=>a.ReadDeletionOperation(r.AttemptId));Assert.Equal(before,f.Native.Calls);Assert.Equal(f.Bytes,File.ReadAllBytes(f.Media));
    }
    [Theory]
    [InlineData(false)][InlineData(true)]
    public async Task MissingOrAlreadySettledPreparationCannotBeConsumed(bool settled)
    {
        using var f=new LocalRetentionDeletionFixture();var io=new DeletionLedgerFixture.Io{OnStep=(stage,_,_)=>{if(stage=="prepare_after_close")f.Db.Corrupt(settled?"UPDATE ordinary_deletion_operations SET phase='settled',progress=0,reason=2,resources_retired=1,settled_ticks=prepared_ticks;":"DELETE FROM ordinary_deletion_operations;");}};
        using var a=f.Service(ledgerDatabase:io);var s=a.Request(f.Row.RecordingId);var p=await f.Approve(a,s);var r=await LocalRetentionDeletionFixture.Finish(a.RequestDeletion(p,f.Row.RecordingId),s);Assert.False(r.ApprovalConsumed);Assert.Equal(0,f.Native.Calls);
    }
    [Fact]
    public async Task ReaderPaginatesAtMost100AndCopiedLedgerRowsDoNotGrantPermission()
    {
        using var f=new LocalRetentionDeletionFixture();using var a=f.Service();var s=a.Request(f.Row.RecordingId);var p=await f.Approve(a,s);await LocalRetentionDeletionFixture.Finish(a.RequestDeletion(p,f.Row.RecordingId),s);
        var cols=SqliteSchemaV43.BindingColumns;using var c=f.Writer();using var cmd=c.CreateCommand();
        cmd.CommandText="WITH RECURSIVE n(i) AS (VALUES(1) UNION ALL SELECT i+1 FROM n WHERE i<101) INSERT INTO ordinary_deletion_operations("+string.Join(',',cols)+",phase) SELECT "+string.Join(',',cols.Select(n=>n is "operation_id" or "approval_id"?"lower(hex(randomblob(16)))":n=="host_id"?"'ffffffffffffffffffffffffffffffff'":n))+",'prepared_unknown' FROM ordinary_deletion_operations CROSS JOIN n WHERE phase='settled';";Assert.Equal(101,cmd.ExecuteNonQuery());
        var first=a.ReadDeletionOperations();var second=a.ReadDeletionOperations(first[^1].Binding.OperationId);Assert.Equal(100,first.Count);Assert.Equal(2,second.Count);Assert.Empty(a.ReadDeletionOperations(second[^1].Binding.OperationId));
        Assert.Equal(102,first.Concat(second).Select(x=>x.Binding.OperationId).Distinct().Count());Assert.Equal(LocalDeletionProgress.NotAttempted,(await a.RequestDeletion(null,f.Row.RecordingId).Response).Progress);Assert.Equal(1,f.Native.Calls);
    }
    [Fact]
    public async Task LedgerFullDoesNotChangeExistingOrSubsequentBusinessConnectionNormalPolicy()
    {
        using var f=new LocalRetentionDeletionFixture();using var c=f.Db.Store.OpenConnection();
        long Sync(){using var cmd=c.CreateCommand();cmd.CommandText="PRAGMA synchronous;";return (long)cmd.ExecuteScalar()!;}
        Assert.Equal(1,Sync());using var a=f.Service();var s=a.Request(f.Row.RecordingId);var p=await f.Approve(a,s);await LocalRetentionDeletionFixture.Finish(a.RequestDeletion(p,f.Row.RecordingId),s);Assert.Equal(1,Sync());
        using var next=f.Db.Store.OpenConnection();using var query=next.CreateCommand();query.CommandText="PRAGMA synchronous;";Assert.Equal(1L,query.ExecuteScalar());
    }
    [Fact]
    public async Task ActualNativeBorrowSeesCommittedBindingThroughSecondReadonlyConnectionAndWriterBusy()
    {
        using var f=new LocalRetentionDeletionFixture();var io=new DeletionLedgerFixture.Io();using var a=f.Service(ledgerDatabase:io);var s=a.Request(f.Row.RecordingId);var p=await f.Approve(a,s);
        DeletionLedgerEntry? captured=null;using var writer=f.Writer();
        f.Native.BeforeRequest=_=>
        {
            Assert.Equal(System.Data.ConnectionState.Closed,io.LastConnection!.State);Assert.Null(io.LastConnection.Handle);
            captured=Assert.Single(DeletionLedgerFixture.Reader(f).Page());var b=captured.Binding;
            Assert.Equal(p.ApprovalId,b.ApprovalId);Assert.Equal(f.Anchor,b.Anchor);Assert.Equal(f.Videos,b.Root);Assert.Equal(s.Facts!.Cutoff,b.Cutoff);Assert.Equal(s.Facts.CompletedAtUtc,b.Completed);
            Assert.Equal(DeletionRecoveryState.PendingLive,captured.State);Assert.Null(captured.Progress);f.AssertProtected();
            Assert.Equal(5,Assert.Throws<Microsoft.Data.Sqlite.SqliteException>(()=>RetentionGuardFixture.Write(writer,"UPDATE ordinary_recording_history SET source_title='ledger_busy';")).SqliteErrorCode);
        };
        var r=await LocalRetentionDeletionFixture.Finish(a.RequestDeletion(p,f.Row.RecordingId),s);
        Assert.Equal(1,f.Native.Calls);Assert.Equal(DeletionLedgerCommit.Committed,r.Preparation);Assert.Equal(DeletionLedgerCommit.Committed,r.Settlement);
        var durable=DeletionLedgerFixture.Reader(f).Read(r.AttemptId)!;Assert.Equal(captured!.Binding,durable.Binding);Assert.Equal(LocalDeletionProgress.DispositionAccepted,durable.Progress);Assert.True(durable.OurResourcesRetired);
        Assert.Equal(DeletionRecoveryState.Settled,durable.State);Assert.Null(DeletionLedgerFixture.Reader(f,"other-sid").Read(r.AttemptId));
        RetentionGuardFixture.Write(writer,"UPDATE ordinary_recording_history SET source_title='ledger_busy';");Assert.Equal(2,io.Commits);
    }
    [Theory]
    [InlineData("UPDATE ordinary_deletion_operations SET file_id=zeroblob(16);")]
    [InlineData("UPDATE ordinary_deletion_operations SET volume_id=zeroblob(8);")]
    [InlineData("UPDATE ordinary_deletion_operations SET recorded_bytes=recorded_bytes+1;")]
    [InlineData("UPDATE ordinary_deletion_operations SET completed_ticks=completed_ticks+1;")]
    [InlineData("UPDATE ordinary_deletion_operations SET owner_session='other';")]
    [InlineData("UPDATE ordinary_deletion_operations SET host_id=lower(hex(randomblob(16)));")]
    [InlineData("UPDATE ordinary_deletion_operations SET videos_root=videos_root||'.other';")]
    [InlineData("UPDATE ordinary_deletion_operations SET output_path=output_path||char(0)||'hidden';")]
    public async Task SameGuardTransactionRejectsTamperedPreparedBinding(string sql)
    {
        using var f=new LocalRetentionDeletionFixture();var io=new DeletionLedgerFixture.Io{OnStep=(stage,_,_)=>{if(stage=="prepare_after_close")DeletionLedgerFixture.CorruptBinding(f,sql);}};
        using var a=f.Service(ledgerDatabase:io);var s=a.Request(f.Row.RecordingId);var p=await f.Approve(a,s);var r=await LocalRetentionDeletionFixture.Finish(a.RequestDeletion(p,f.Row.RecordingId),s);
        Assert.False(r.ApprovalConsumed);Assert.Equal(LocalDeletionProgress.NotAttempted,r.Progress);Assert.Equal(0,f.Native.Calls);Assert.DoesNotContain(io.Calls,x=>x.Stage=="settle_before_commit");
    }
    [Theory]
    [InlineData(0)][InlineData(101)][InlineData(-1)]
    public void PageLimitAndIdAreBoundedWithoutMediaIo(int limit)
    {
        using var f=new LocalRetentionDeletionFixture();Assert.Throws<ArgumentException>(()=>DeletionLedgerFixture.Reader(f).Page(limit:limit));Assert.Equal(0,f.Native.Calls);Assert.Empty(f.Files.Api.Opened);
    }
    [Theory]
    [InlineData("")][InlineData("../unsafe")][InlineData("AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA")][InlineData("0000000000000000000000000000000\0")]
    public void ExactOperationIdCannotBeExecutableOrMalformedMaterial(string id)
    {using var f=new LocalRetentionDeletionFixture();Assert.Throws<ArgumentException>(()=>DeletionLedgerFixture.Reader(f).Read(id));Assert.Equal(0,f.Native.Calls);}
    [Fact]
    public async Task ExistingWriteTransactionMakesPreparationFailWithoutConsumptionOrSecondNative()
    {
        using var f=new LocalRetentionDeletionFixture();using var a=f.Service();var s=a.Request(f.Row.RecordingId);var p=await f.Approve(a,s);
        using var writer=f.Writer();using(var t=writer.BeginTransaction(deferred:false))
        {
            var r=await LocalRetentionDeletionFixture.Finish(a.RequestDeletion(p,f.Row.RecordingId),s);Assert.False(r.ApprovalConsumed);Assert.Equal(0,f.Native.Calls);Assert.Equal(DeletionLedgerCommit.NotCommitted,r.Preparation);
        }
        Assert.Empty(DeletionLedgerFixture.Reader(f).Page());
    }
    [Fact]
    public async Task OldExecuteDoesNotWriteLedgerOrPerformFileActionAndLedgerRowCannotAuthorize()
    {
        using var f=new LocalRetentionDeletionFixture();using var a=f.Service();var s=a.Request(f.Row.RecordingId);var p=await f.Approve(a,s);
        var r=await a.Execute(p,f.Row.RecordingId,_=>LocalRetentionAttemptResult.Completed);Assert.False(r.FileActionPerformed);Assert.Equal(0,f.Native.Calls);
        Assert.True(await s.Retirement.WaitAsync(TimeSpan.FromSeconds(5)));Assert.Empty(DeletionLedgerFixture.Reader(f).Page());
        Assert.Equal(LocalDeletionProgress.NotAttempted,(await a.RequestDeletion(p,f.Row.RecordingId).Response).Progress);
        Assert.All(typeof(DeletionLedger.Prepared).GetConstructors(System.Reflection.BindingFlags.NonPublic|System.Reflection.BindingFlags.Instance),c=>Assert.True(c.IsPrivate));
    }
}
