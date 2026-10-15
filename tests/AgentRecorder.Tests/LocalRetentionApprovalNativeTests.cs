using AgentRecorder.Infrastructure;
using AgentRecorder.Persistence;
using Xunit;
using Phase=AgentRecorder.Tests.OriginalFileHoldFixture.Phase;

namespace AgentRecorder.Tests;

[Collection("NonParallel-AgentRecorderDataDir")]
public sealed class LocalRetentionApprovalNativeTests
{
    [Fact]
    public async Task OwnedOriginalMediaRetainsBytesAndIdentityAcrossAuthorizedAttemptAndWalBusyControl()
    {
        using var f=new LocalRetentionApprovalFixture(native:true);using var a=f.Service();var s=a.Request(f.Row.RecordingId);var p=await f.Approve(a,s);using var phase=new Phase();var calls=0;
        var attempt=a.Execute(p,f.Row.RecordingId,context=>{context.RequireActive();calls++;phase.Block();return LocalRetentionAttemptResult.Completed;});
        try
        {
            await phase.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));OrdinaryPreparationFixture.AssertConflict(f.Engine,f.Media);
            using var writer=LocalRetentionApprovalLifecycleTests.Writer(f);
            Assert.Equal(5,Assert.Throws<Microsoft.Data.Sqlite.SqliteException>(()=>RetentionGuardFixture.Write(writer,"UPDATE ordinary_recording_history SET source_title='native_control';")).SqliteErrorCode);
            Assert.Throws<IOException>(()=>File.ReadAllBytes(f.Media));phase.Release();var r=await attempt;
            Assert.True(r.ApprovalConsumed);Assert.False(r.FileActionPerformed);Assert.True(r.ExecutionRetirement!.Retired);Assert.Equal(1,calls);
            RetentionGuardFixture.Write(writer,"UPDATE ordinary_recording_history SET source_title='native_control';");Assert.True(await s.Retirement.WaitAsync(TimeSpan.FromSeconds(5)));
            Assert.Equal(Enumerable.Range(0,12).Select(i=>(byte)i),File.ReadAllBytes(f.Media));
            Assert.Equal(f.Row.PublicationReceipt!.Anchor.Identity,WindowsReadOnlyFileMetadataProbe.Instance.Observe(f.Media).Before!.Identity);
        }
        finally{phase.Release();await attempt;}
    }
}
