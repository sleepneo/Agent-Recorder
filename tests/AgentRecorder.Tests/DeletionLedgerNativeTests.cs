using AgentRecorder.Persistence;
using Xunit;

namespace AgentRecorder.Tests;

[Collection("NonParallel-AgentRecorderDataDir")]
public sealed class DeletionLedgerNativeTests
{
    [Theory]
    [InlineData(false)][InlineData(true)]
    public async Task OwnedNativeObjectHasCommittedLedgerBeforeCallAndIndependentFinalRead(bool readOnly)
    {
        using var f=new LocalRetentionDeletionFixture(real:true);if(readOnly)File.SetAttributes(f.Media,FileAttributes.ReadOnly);
        try
        {
            var io=new DeletionLedgerFixture.Io();using var a=f.Service(ledgerDatabase:io);var s=a.Request(f.Row.RecordingId);var p=await f.Approve(a,s);
            f.Native.BeforeRequest=_=>
            {
                var row=Assert.Single(DeletionLedgerFixture.Reader(f).Page());Assert.Equal(DeletionRecoveryState.PendingLive,row.State);Assert.Equal(f.Anchor,row.Binding.Anchor);Assert.Equal(p.ApprovalId,row.Binding.ApprovalId);f.AssertProtected();
                using var writer=f.Writer();Assert.Equal(5,Assert.Throws<Microsoft.Data.Sqlite.SqliteException>(()=>RetentionGuardFixture.Write(writer,"UPDATE ordinary_recording_history SET source_title='native_ledger';")).SqliteErrorCode);
            };
            var r=await LocalRetentionDeletionFixture.Finish(a.RequestDeletion(p,f.Row.RecordingId),s);Assert.Equal(DeletionLedgerCommit.Committed,r.Preparation);Assert.Equal(DeletionLedgerCommit.Committed,r.Settlement);
            var durable=DeletionLedgerFixture.Reader(f).Read(r.AttemptId)!;Assert.Equal(r.Progress,durable.Progress);Assert.True(durable.OurResourcesRetired);Assert.Equal(1,f.Native.Calls);
            if(readOnly){Assert.Equal(LocalDeletionProgress.DispositionRejected,r.Progress);Assert.Equal(5,durable.Win32Error);}
            else{Assert.Equal(LocalDeletionProgress.DispositionAccepted,r.Progress);Assert.True(f.Native.DeletePending);f.AssertNotAddressable();}
            Assert.Equal(1,f.Db.Scalar("SELECT count(*) FROM ordinary_recording_history WHERE status_code='completed';"));Assert.Equal(1,f.Db.Scalar("SELECT count(*) FROM ordinary_publication_anchors;"));
        }
        finally{if(readOnly)File.SetAttributes(f.Media,FileAttributes.Normal);}
    }
}
