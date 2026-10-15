using AgentRecorder.Infrastructure;
using AgentRecorder.Persistence;
using Xunit;
using Phase=AgentRecorder.Tests.OriginalFileHoldFixture.Phase;

namespace AgentRecorder.Tests;

[Collection("NonParallel-AgentRecorderDataDir")]
public sealed class LocalRetentionApprovalLifecycleTests
{
    [Theory]
    [InlineData(1)][InlineData(2)][InlineData(3)][InlineData(4)]
    public async Task NegativeLocalDecisionCannotIssueOrConsume(int value)
    {
        var choice=(LocalRetentionUiChoice)value;
        using var f=new LocalRetentionApprovalFixture();using var a=f.Service();var s=a.Request(f.Row.RecordingId);await f.Adapter.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(LocalRetentionState.Pending,s.Current.State);f.Adapter.Decide(choice);f.Adapter.Retire.TrySetResult(true);
        var r=await s.Response.WaitAsync(TimeSpan.FromSeconds(5));Assert.Equal(choice==LocalRetentionUiChoice.Unavailable?LocalRetentionState.Unavailable:LocalRetentionState.Rejected,r.State);
        Assert.Null(r.Permit);Assert.False(r.FileActionPerformed);Assert.True(await s.Retirement.WaitAsync(TimeSpan.FromSeconds(5)));Assert.Empty(f.Files.Api.Calls);
    }

    [Fact]
    public async Task WaitingForUiHoldsNoFileReservationOrWriteTransactionAndHumanBudgetIsSeparate()
    {
        using var f=new LocalRetentionApprovalFixture();using var a=f.Service();
        f.Adapter.OnBegin=()=>
        {
            var reader=(LocalRetentionFactsReader)typeof(LocalRetentionApproval).GetField("_reader",System.Reflection.BindingFlags.Instance|System.Reflection.BindingFlags.NonPublic)!.GetValue(a)!;
            Assert.True(reader.Retired); // Actual provider Closed/null handle and detached transaction, not just WAL writer progress.
            using var c=Writer(f);RetentionGuardFixture.Write(c,"UPDATE ordinary_recording_history SET source_title='UI_waiting';");
            Assert.False(RuntimeOutputTestEngine.Read(f.Engine,f.Media)!.Paths.Single().InUse);Assert.Empty(f.Files.Api.Calls);
            Assert.Equal(Enumerable.Range(0,12).Select(i=>(byte)i),File.ReadAllBytes(f.Media));
        };
        var s=a.Request(f.Row.RecordingId);await f.Adapter.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));f.Time.Advance(59);
        var p=await f.Approve(a,s);f.Time.Advance(59);
        Assert.Equal(LocalRetentionState.Consumed,(await a.Execute(p,f.Row.RecordingId,_=>LocalRetentionAttemptResult.Completed)).State);
        Assert.True(await s.Retirement.WaitAsync(TimeSpan.FromSeconds(5)));
    }

    [Theory]
    [InlineData(false)][InlineData(true)]
    public async Task ConfirmationAndApprovalEachExpireWithoutExtendingOnLateDecision(bool approved)
    {
        using var f=new LocalRetentionApprovalFixture();using var a=f.Service();var s=a.Request(f.Row.RecordingId);await f.Adapter.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        LocalRetentionApproval.Permit? permit=approved?await f.Approve(a,s):null;f.Time.Advance(60);
        Assert.Equal(LocalRetentionState.Expired,s.Current.State);
        if(!approved){Assert.Equal(LocalRetentionState.Expired,(await s.Response).State);Assert.Equal(LocalRetentionState.Busy,(await a.Request(f.Row.RecordingId).Response).State);f.Adapter.Decide(LocalRetentionUiChoice.Approve);f.Adapter.Retire.TrySetResult(true);}
        Assert.True(await s.Retirement.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Equal(LocalRetentionState.ExecutionRejected,(await a.Execute(permit,f.Row.RecordingId,_=>throw new InvalidOperationException())).State);Assert.Empty(f.Files.Api.Calls);
    }

    [Theory]
    [InlineData(false,-1)][InlineData(false,61)][InlineData(true,-1)][InlineData(true,61)]
    public async Task UtcRollbackOrForwardJumpNeverExtendsEitherWindow(bool approved,int seconds)
    {
        using var f=new LocalRetentionApprovalFixture();using var a=f.Service();var s=a.Request(f.Row.RecordingId);await f.Adapter.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var permit=approved?await f.Approve(a,s):null;f.Time.ShiftUtc(seconds);
        if(approved){Assert.Equal(LocalRetentionState.Expired,s.Current.State);Assert.Equal(LocalRetentionState.ExecutionRejected,(await a.Execute(permit,f.Row.RecordingId,_=>throw new InvalidOperationException())).State);s.Dispose();}
        else {f.Adapter.Decide(LocalRetentionUiChoice.Approve);f.Adapter.Retire.TrySetResult(true);Assert.Equal(LocalRetentionState.Expired,(await s.Response.WaitAsync(TimeSpan.FromSeconds(5))).State);}
        Assert.True(await s.Retirement.WaitAsync(TimeSpan.FromSeconds(5)));Assert.Empty(f.Files.Api.Calls);
    }

    [Theory]
    [InlineData("cancel")][InlineData("host")][InlineData("expire")]
    public async Task UiResponseDoesNotSubstituteForThreadRetirement(string stop)
    {
        using var f=new LocalRetentionApprovalFixture();using var cancel=new CancellationTokenSource();using var a=f.Service();var s=a.Request(f.Row.RecordingId,cancel.Token);
        await f.Adapter.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));f.Adapter.Decide(LocalRetentionUiChoice.Approve);
        if(stop=="cancel")cancel.Cancel();if(stop=="host")a.Dispose();if(stop=="expire")f.Time.Advance(60);
        var response=await s.Response.WaitAsync(TimeSpan.FromSeconds(5));Assert.Null(response.Permit);Assert.NotEqual(LocalRetentionState.Approved,response.State);
        Assert.False(s.Retirement.IsCompleted);using var other=f.Service();Assert.Equal(LocalRetentionState.Busy,(await other.Request(f.Row.RecordingId).Response).State);
        f.Adapter.Retire.TrySetResult(true);Assert.True(await s.Retirement.WaitAsync(TimeSpan.FromSeconds(5)));Assert.Empty(f.Files.Api.Calls);
    }

    [Fact]
    public async Task LateDuplicateAndWrongIntentCannotApproveNextTarget()
    {
        using var f=new LocalRetentionApprovalFixture();using var a=f.Service();var first=a.Request(f.Row.RecordingId);await f.Adapter.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        f.Adapter.Decide(LocalRetentionUiChoice.Reject);f.Adapter.Retire.TrySetResult(true);await first.Response;Assert.True(await first.Retirement.WaitAsync(TimeSpan.FromSeconds(5)));
        var stale=f.Adapter.Intent;Assert.False(f.Adapter.Decide(LocalRetentionUiChoice.Approve));
        var second=a.Request(f.Row.RecordingId);var result=await second.Response.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.NotSame(stale,f.Adapter.Intent);Assert.NotEqual(stale.Nonce,f.Adapter.Intent.Nonce);Assert.Equal(LocalRetentionState.Unavailable,result.State);Assert.Null(result.Permit);
        Assert.True(await second.Retirement.WaitAsync(TimeSpan.FromSeconds(5)));Assert.Empty(f.Files.Api.Calls);
    }

    [Fact]
    public async Task UnknownUiExitRetainsAuthorityAdmissionAndNoPermit()
    {
        using var f=new LocalRetentionApprovalFixture();using var a=f.Service();var s=a.Request(f.Row.RecordingId);await f.Adapter.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        f.Adapter.Decide(LocalRetentionUiChoice.Approve);f.Adapter.Retire.TrySetResult(false);
        Assert.Equal(LocalRetentionState.Unavailable,(await s.Response.WaitAsync(TimeSpan.FromSeconds(5))).State);Assert.False(await s.Retirement.WaitAsync(TimeSpan.FromSeconds(5)));
        using var other=f.Service();Assert.Equal(LocalRetentionState.Busy,(await other.Request(f.Row.RecordingId).Response).State);Assert.Empty(f.Files.Api.Calls);
    }

    [Theory]
    [InlineData("callback")][InlineData("timeout")][InlineData("cancel")]
    public async Task OnceConsumedFailureOrCancellationNeverRestoresPermit(string fault)
    {
        using var f=new LocalRetentionApprovalFixture();using var a=f.Service();using var cancel=new CancellationTokenSource();var s=a.Request(f.Row.RecordingId,cancel.Token);var p=await f.Approve(a,s);var calls=0;
        var result=await a.Execute(p,f.Row.RecordingId,_=>{calls++;if(fault=="callback")throw new IOException("PRIVATE_CALLBACK");if(fault=="timeout")f.Time.Advance(3.1);else cancel.Cancel();return LocalRetentionAttemptResult.Completed;});
        Assert.True(result.ApprovalConsumed);Assert.False(result.FileActionPerformed);Assert.NotEqual(RetentionComposedReason.Checked,result.Execution!.Reason);Assert.Equal(1,calls);
        Assert.True(await s.Retirement.WaitAsync(TimeSpan.FromSeconds(5)));Assert.Equal(LocalRetentionState.ExecutionRejected,(await a.Execute(p,f.Row.RecordingId,_=>throw new InvalidOperationException())).State);
    }

    [Theory]
    [InlineData("rollback")][InlineData("transaction_dispose")][InlineData("close")][InlineData("connection_dispose")][InlineData("native")]
    public async Task ConsumedUnknownRetirementRetainsAdmissionAndNeverRetries(string fault)
    {
        using var f=new LocalRetentionApprovalFixture();var db=new RetentionGuardFixture.TraceDatabase{Fault=fault=="native"?null:fault};
        if(fault=="native")f.Files.Api.CloseValue=n=>n.Leaf?OriginalFileCloseOutcome.Unknown:OriginalFileCloseOutcome.Closed;
        using var a=f.Service(database:db);var s=a.Request(f.Row.RecordingId);var p=await f.Approve(a,s);var calls=0;
        var r=await a.Execute(p,f.Row.RecordingId,_=>{calls++;return LocalRetentionAttemptResult.Completed;});Assert.Equal(LocalRetentionState.Unknown,r.State);Assert.True(r.ApprovalConsumed);Assert.False(r.FileActionPerformed);
        Assert.False(await s.Retirement.WaitAsync(TimeSpan.FromSeconds(5)));Assert.Equal(1,calls);using var other=f.Service();Assert.Equal(LocalRetentionState.Busy,(await other.Request(f.Row.RecordingId).Response).State);
        Assert.Equal(LocalRetentionState.ExecutionRejected,(await a.Execute(p,f.Row.RecordingId,_=>throw new InvalidOperationException())).State);
    }

    [Fact]
    public async Task ConcurrentExecutionAndCancelBeforeConsumptionHaveExactLinearization()
    {
        using var f=new LocalRetentionApprovalFixture();using var phase=new Phase();using var a=f.Service();var s=a.Request(f.Row.RecordingId);var p=await f.Approve(a,s);var calls=0;
        var attempt=a.Execute(p,f.Row.RecordingId,_=>{Interlocked.Increment(ref calls);phase.Block();return LocalRetentionAttemptResult.Completed;});
        try
        {
            await phase.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var others=await Task.WhenAll(Enumerable.Range(0,8).Select(_=>a.Execute(p,f.Row.RecordingId,_=>throw new InvalidOperationException())));
            Assert.All(others,r=>Assert.Equal(LocalRetentionState.ExecutionRejected,r.State));s.Dispose();Assert.False(s.Retirement.IsCompleted);
            phase.Release();var result=await attempt;Assert.True(result.ApprovalConsumed);Assert.Equal(1,calls);Assert.True(await s.Retirement.WaitAsync(TimeSpan.FromSeconds(5)));
        }
        finally{phase.Release();await attempt;}
    }

    [Fact]
    public async Task CancellationBeforeDatabaseBorrowWinsWithZeroAttemptCallbacks()
    {
        using var f=new LocalRetentionApprovalFixture();using var phase=new Phase();var db=new RetentionGuardFixture.TraceDatabase{BeforeOpen=phase.Block};using var a=f.Service(database:db);
        var s=a.Request(f.Row.RecordingId);var p=await f.Approve(a,s);var calls=0;var attempt=a.Execute(p,f.Row.RecordingId,_=>{calls++;return LocalRetentionAttemptResult.Completed;});
        try{await phase.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));s.Dispose();Assert.False(s.Retirement.IsCompleted);phase.Release();var r=await attempt;Assert.False(r.ApprovalConsumed);Assert.Equal(0,calls);Assert.True(await s.Retirement.WaitAsync(TimeSpan.FromSeconds(5)));}
        finally{phase.Release();await attempt;}
    }

    internal static Microsoft.Data.Sqlite.SqliteConnection Writer(LocalRetentionApprovalFixture f)
    {var c=new Microsoft.Data.Sqlite.SqliteConnection(new Microsoft.Data.Sqlite.SqliteConnectionStringBuilder{DataSource=f.Db.Store.DatabasePath,Mode=Microsoft.Data.Sqlite.SqliteOpenMode.ReadWrite,Pooling=false,DefaultTimeout=1}.ToString());c.Open();return c;}

    [Fact]
    public async Task NormalRecordingRegistrationRemainsAvailableWhileHumanIsThinking()
    {
        using var f=new LocalRetentionApprovalFixture();using var a=f.Service();var s=a.Request(f.Row.RecordingId);await f.Adapter.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var tray=new OrdinaryPreparationEngineFixture.Tray();var backend=new OrdinaryPreparationEngineFixture.Backend();f.Engine.SetTray(tray);f.Engine.BackendFactory=_=>(backend,"fake");
        f.Engine.CreateRecording(new System.Text.Json.Nodes.JsonObject{
            ["source"]=new System.Text.Json.Nodes.JsonObject{["type"]="display",["display_id"]="display_1"},["video"]=new System.Text.Json.Nodes.JsonObject{["fps"]=15},
            ["duration_seconds"]=60,["countdown_seconds"]=1,["output"]=new System.Text.Json.Nodes.JsonObject{["directory"]=f.Videos,["filename"]=Path.GetFileName(f.Media),["conflict_policy"]="rename"}},"local-test",tray);
        Assert.Single(f.Engine._recs);Assert.Equal(1,tray.Requests);Assert.Equal(0,backend.Starts);Assert.Empty(f.Files.Api.Calls);
        f.Adapter.Decide(LocalRetentionUiChoice.Reject);f.Adapter.Retire.TrySetResult(true);Assert.Equal(LocalRetentionState.Rejected,(await s.Response.WaitAsync(TimeSpan.FromSeconds(5))).State);
        Assert.True(await s.Retirement.WaitAsync(TimeSpan.FromSeconds(5)));
    }

    [Fact]
    public async Task LocalDecisionFactoryCannotIssueBeforeActualUiDeliveryAndRetirement()
    {
        using var f=new LocalRetentionApprovalFixture();using var a=f.Service();var s=a.Request(f.Row.RecordingId);await f.Adapter.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Null(LocalRetentionApproval.Permit.CompleteLocalDecision(s,new(f.Adapter.Intent,LocalRetentionUiChoice.Approve),f.Time.GetUtcNow(),f.Time.GetTimestamp()));
        var permit=await f.Approve(a,s);Assert.False(f.Adapter.Decide(LocalRetentionUiChoice.Approve));
        Assert.Null(LocalRetentionApproval.Permit.CompleteLocalDecision(s,s.RetiredDecision!,f.Time.GetUtcNow(),f.Time.GetTimestamp()));
        s.Dispose();Assert.True(await s.Retirement.WaitAsync(TimeSpan.FromSeconds(5)));Assert.Equal(LocalRetentionState.ExecutionRejected,(await a.Execute(permit,f.Row.RecordingId,_=>throw new InvalidOperationException())).State);
    }

    [Theory]
    [InlineData(false)][InlineData(true)]
    public async Task HostLifecycleTokenRejectsLateApprovalAndRetainsActualUiUntilExit(bool before)
    {
        using var f=new LocalRetentionApprovalFixture();using var stop=new CancellationTokenSource();if(before)stop.Cancel();using var a=f.Service(hostStopping:stop.Token);var s=a.Request(f.Row.RecordingId);
        if(!before){await f.Adapter.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));stop.Cancel();Assert.False(s.Retirement.IsCompleted);Assert.True(f.Adapter.Token.IsCancellationRequested);f.Adapter.Decide(LocalRetentionUiChoice.Approve);f.Adapter.Retire.TrySetResult(true);}
        Assert.Equal(LocalRetentionState.Cancelled,(await s.Response.WaitAsync(TimeSpan.FromSeconds(5))).State);Assert.True(await s.Retirement.WaitAsync(TimeSpan.FromSeconds(5)));Assert.Empty(f.Files.Api.Calls);
    }

    [Theory]
    [InlineData(false)][InlineData(true)]
    public async Task UiTaskFailureWaitsForActualRetirementAndDoesNotApprove(bool retirementThrows)
    {
        using var f=new LocalRetentionApprovalFixture();using var a=f.Service();var s=a.Request(f.Row.RecordingId);await f.Adapter.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        f.Adapter.Result.TrySetException(new IOException("PRIVATE_UI_THREAD"));Assert.Equal(LocalRetentionState.Unavailable,(await s.Response.WaitAsync(TimeSpan.FromSeconds(5))).State);
        Assert.False(s.Retirement.IsCompleted);if(retirementThrows)f.Adapter.Retire.TrySetException(new IOException("PRIVATE_EXIT"));else f.Adapter.Retire.TrySetResult(true);
        Assert.Equal(!retirementThrows,await s.Retirement.WaitAsync(TimeSpan.FromSeconds(5)));Assert.Empty(f.Files.Api.Calls);
    }

    [Fact]
    public async Task LateUiRetirementDoesNotRestartApprovalSixtySecondBudget()
    {
        using var f=new LocalRetentionApprovalFixture();using var a=f.Service();var s=a.Request(f.Row.RecordingId);await f.Adapter.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        f.Adapter.Decide(LocalRetentionUiChoice.Approve);
        var observed=typeof(LocalRetentionApproval.Session).GetField("_receivedDecision",System.Reflection.BindingFlags.Instance|System.Reflection.BindingFlags.NonPublic)!;
        Assert.True(await Task.Run(()=>SpinWait.SpinUntil(()=>observed.GetValue(s) is not null,TimeSpan.FromSeconds(2))));
        f.Time.Advance(30);f.Adapter.Retire.TrySetResult(true);var r=await s.Response.WaitAsync(TimeSpan.FromSeconds(5));Assert.Equal(LocalRetentionState.Approved,r.State);
        f.Time.Advance(30);Assert.True(await s.Retirement.WaitAsync(TimeSpan.FromSeconds(5)));Assert.Equal(LocalRetentionState.Expired,s.Current.State);
        Assert.Equal(LocalRetentionState.ExecutionRejected,(await a.Execute(r.Permit,f.Row.RecordingId,_=>throw new InvalidOperationException())).State);
    }

    [Fact]
    public async Task ExecuteEstablishesCommonScopeBeforeReturningAndDoesNotRefreshDispatchBudget()
    {
        using var f=new LocalRetentionApprovalFixture();using var phase=new Phase();var db=new RetentionGuardFixture.TraceDatabase{BeforeOpen=phase.Block};using var a=f.Service(database:db);
        var s=a.Request(f.Row.RecordingId);var p=await f.Approve(a,s);var calls=0;
        var attempt=a.Execute(p,f.Row.RecordingId,_=>{calls++;return LocalRetentionAttemptResult.Completed;});
        try
        {
            var field=typeof(LocalRetentionApproval.Session).GetField("_execution",System.Reflection.BindingFlags.Instance|System.Reflection.BindingFlags.NonPublic)!;
            var composition=Assert.IsType<RetentionComposedScope.Session>(field.GetValue(s));
            await phase.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));f.Time.Advance(3.1);
            Assert.Equal(RetentionComposedReason.TimedOut,(await composition.Response.WaitAsync(TimeSpan.FromSeconds(5))).Reason);
            Assert.False(s.Retirement.IsCompleted);phase.Release();var result=await attempt;
            Assert.False(result.ApprovalConsumed);Assert.False(result.FileActionPerformed);Assert.Equal(0,calls);Assert.True(await s.Retirement.WaitAsync(TimeSpan.FromSeconds(5)));
        }
        finally{phase.Release();await attempt;}
    }
}
