using AgentRecorder.Persistence;
using Xunit;
using Phase=AgentRecorder.Tests.OriginalFileHoldFixture.Phase;

namespace AgentRecorder.Tests;

[Collection("NonParallel-AgentRecorderDataDir")]
public sealed class LocalRetentionFactsDeadlineTests
{
    [Theory]
    [InlineData(true)][InlineData(false)]
    public async Task ExistenceReturnAfterDeadlineOrCancellationCannotIssueFacts(bool timeout)
    {
        using var f=new LocalRetentionDeletionFixture();using var cancel=new CancellationTokenSource();
        var reader=new LocalRetentionFactsReader(f.Db.Store,f.Db.Ordinary,UnifiedHistoryTestDatabase.Sid,UnifiedHistoryTestDatabase.Session,f.Videos,f.Clock,
            path=>{Assert.True(File.Exists(path));if(timeout)f.Clock.Advance(3.1);else cancel.Cancel();return true;});
        if(timeout)await Assert.ThrowsAsync<TimeoutException>(()=>Task.Run(()=>reader.Read(f.Row.RecordingId,cancel.Token)));
        else await Assert.ThrowsAsync<OperationCanceledException>(()=>Task.Run(()=>reader.Read(f.Row.RecordingId,cancel.Token)));
        Assert.True(reader.Retired);Assert.Equal(0,f.Ui.Begins);Assert.Equal(0,f.Native.Calls);
    }

    [Theory]
    [InlineData(true)][InlineData(false)]
    public async Task SlowExistenceKeepsAdmissionUntilActualQueryReturnsAndNeverStartsUi(bool timeout)
    {
        using var f=new LocalRetentionDeletionFixture();using var phase=new Phase();using var a=f.Service(existence:path=>{phase.Block();if(timeout)f.Clock.Advance(3.1);return File.Exists(path);});
        var s=a.Request(f.Row.RecordingId);
        try
        {
            await phase.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            if(timeout)Assert.Equal(LocalRetentionState.Unavailable,(await s.Response.WaitAsync(TimeSpan.FromSeconds(5))).State);
            else{s.Dispose();Assert.Equal(LocalRetentionState.Cancelled,(await s.Response).State);}
            Assert.False(s.Retirement.IsCompleted);using var other=f.Service();Assert.Equal(LocalRetentionState.Busy,(await other.Request(f.Row.RecordingId).Response).State);
            phase.Release();Assert.True(await s.Retirement.WaitAsync(TimeSpan.FromSeconds(5)));Assert.Equal(0,f.Ui.Begins);Assert.Equal(0,f.Native.Calls);
        }
        finally{phase.Release();s.Dispose();await s.Retirement.WaitAsync(TimeSpan.FromSeconds(5));}
    }
}
