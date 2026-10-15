using System.Reflection;
using System.Text.Json;
using AgentRecorder.Infrastructure;
using AgentRecorder.Persistence;
using Xunit;

namespace AgentRecorder.Tests;

[Collection("NonParallel-AgentRecorderDataDir")]
public sealed class LocalRetentionApprovalContractTests
{
    [Fact]
    public async Task ValidLocalDecisionConsumesExactlyOnceInsideActualCompositionAndExpiresContext()
    {
        using var f=new LocalRetentionApprovalFixture();using var a=f.Service();var session=a.Request(f.Row.RecordingId);var permit=await f.Approve(a,session);
        LocalRetentionApproval.AttemptContext? saved=null;Task<LocalRetentionResult>? reentrant=null;var calls=0;
        var result=await a.Execute(permit,f.Row.RecordingId,context=>
        {
            calls++;saved=context;context.RequireActive();OrdinaryPreparationFixture.AssertConflict(f.Engine,f.Media);
            reentrant=a.Execute(permit,f.Row.RecordingId,_=>throw new InvalidOperationException());Assert.True(reentrant.IsCompletedSuccessfully);
            Exception? error=null;var thread=new Thread(()=>{try{context.RequireActive();}catch(Exception e){error=e;}});thread.Start();Assert.True(thread.Join(TimeSpan.FromSeconds(1)));Assert.IsType<InvalidOperationException>(error);
            return LocalRetentionAttemptResult.Completed;
        });
        Assert.Equal(LocalRetentionState.Consumed,result.State);Assert.True(result.ApprovalConsumed);Assert.False(result.FileActionPerformed);
        Assert.Equal(RetentionComposedReason.Checked,result.Execution!.Reason);Assert.True(result.ExecutionRetirement!.Retired);Assert.Equal(1,calls);
        Assert.Throws<InvalidOperationException>(()=>saved!.RequireActive());Assert.Equal(LocalRetentionState.ExecutionRejected,(await reentrant!).State);
        Assert.True(await session.Retirement.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Equal(LocalRetentionState.ExecutionRejected,(await a.Execute(permit,f.Row.RecordingId,_=>throw new InvalidOperationException())).State);
        OriginalFileScopeFixture.AssertClosedInReverse(f.Files.Api);
        Assert.All(typeof(LocalRetentionApproval.Permit).GetConstructors(BindingFlags.Instance|BindingFlags.Public|BindingFlags.NonPublic),c=>Assert.True(c.IsPrivate));
        Assert.Throws<NotSupportedException>(()=>JsonSerializer.Deserialize<LocalRetentionApproval.Permit>("{}"));
        Assert.Equal(typeof(LocalRetentionApproval.Permit),typeof(LocalRetentionApproval).GetMethod("Execute",BindingFlags.Instance|BindingFlags.NonPublic)!.GetParameters()[0].ParameterType);
    }

    [Theory]
    [InlineData("id")][InlineData("path")][InlineData("size")][InlineData("volume")][InlineData("low")][InlineData("high")][InlineData("completed")][InlineData("cutoff")]
    [InlineData("sid")][InlineData("session")][InlineData("root")][InlineData("store")][InlineData("writer")][InlineData("engine")][InlineData("lifetime")][InlineData("semantics")][InlineData("intent")]
    public async Task EveryFrozenPermitMaterialIsCheckedBeforeDispatch(string kind)
    {
        using var f=new LocalRetentionApprovalFixture();using var a=f.Service();var session=a.Request(f.Row.RecordingId);var permit=await f.Approve(a,session);
        if(kind is "id" or "path" or "size" or "volume" or "low" or "high" or "completed" or "cutoff")
        {
            var facts=f.Adapter.Intent.Facts;var anchor=facts.Anchor;
            var changed=kind switch
            {
                "id"=>facts with{RecordingId="other"},"path"=>facts with{Anchor=anchor with{Path=anchor.Path+".other.mp4"}},
                "size"=>facts with{Anchor=anchor with{Length=13}},"volume"=>facts with{Anchor=anchor with{Identity=anchor.Identity with{VolumeSerialNumber=999}}},
                "low"=>facts with{Anchor=anchor with{Identity=anchor.Identity with{IdLow=999}}},"high"=>facts with{Anchor=anchor with{Identity=anchor.Identity with{IdHigh=999}}},
                "completed"=>facts with{CompletedAtUtc=facts.CompletedAtUtc.AddTicks(1)},_=>facts with{Cutoff=facts.Cutoff.AddTicks(1)}
            };Set(permit,"_facts",changed);
        }
        else Set(permit,"_"+(kind=="session"?"binding":kind),kind switch{"lifetime"=>Guid.NewGuid(),"intent"=>LocalRetentionIntent.ForConfirmedFacts(f.Adapter.Intent.Facts,f.Time),
            "engine" or "store" or "writer"=>new object(),_=>"different"});
        var calls=0;var result=await a.Execute(permit,f.Row.RecordingId,_=>{calls++;return LocalRetentionAttemptResult.Completed;});
        Assert.Equal(LocalRetentionState.ExecutionRejected,result.State);Assert.False(result.ApprovalConsumed);Assert.Equal(0,calls);Assert.Empty(f.Files.Api.Calls);
        session.Dispose();Assert.True(await session.Retirement.WaitAsync(TimeSpan.FromSeconds(5)));
    }

    [Fact]
    public async Task OtherAuthorityAndWrongRequestIdCannotUsePermit()
    {
        using var f=new LocalRetentionApprovalFixture();using var a=f.Service();using var other=f.Service();var session=a.Request(f.Row.RecordingId);var permit=await f.Approve(a,session);
        Assert.Equal(LocalRetentionState.ExecutionRejected,(await other.Execute(permit,f.Row.RecordingId,_=>throw new InvalidOperationException())).State);
        Assert.Equal(LocalRetentionState.ExecutionRejected,(await a.Execute(permit,"Candidate",_=>throw new InvalidOperationException())).State);
        Assert.Equal(LocalRetentionState.ExecutionRejected,(await a.Execute(null,f.Row.RecordingId,_=>throw new InvalidOperationException())).State);
        session.Dispose();Assert.True(await session.Retirement.WaitAsync(TimeSpan.FromSeconds(5)));Assert.Empty(f.Files.Api.Calls);
    }

    [Theory]
    [InlineData("status_code","'failed'")][InlineData("completed_at_utc","NULL")][InlineData("output_size_bytes","0")]
    [InlineData("output_path","CAST(output_path AS BLOB)")][InlineData("source_title","source_title||char(0)||'hidden'")]
    [InlineData("recording_id","recording_id||char(0)||'hidden'")]
    public async Task InvalidFactsNeverDisplayConfirmation(string column,string value)
    {
        using var f=new LocalRetentionApprovalFixture();f.Db.Corrupt($"UPDATE ordinary_recording_history SET {column}={value};");using var a=f.Service();var session=a.Request(f.Row.RecordingId);
        Assert.Equal(LocalRetentionState.Unavailable,(await session.Response.WaitAsync(TimeSpan.FromSeconds(5))).State);Assert.True(await session.Retirement.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Equal(0,f.Adapter.Begins);Assert.Empty(f.Files.Api.Calls);
    }

    [Theory]
    [InlineData("bundle")][InlineData("anchor")][InlineData("schema")][InlineData("sid")][InlineData("binding")][InlineData("root")][InlineData("missing")][InlineData("case")]
    public async Task MissingOrOutOfScopeFactsCannotIssuePermit(string kind)
    {
        using var f=new LocalRetentionApprovalFixture();
        if(kind=="bundle")f.Db.Corrupt("UPDATE ordinary_recording_bundles SET status_code='generating',bundle_path=$path,operation_id=$op;",("$path",AgentRecorder.Core.OrdinaryRecordingBundleOperation.DerivePath(f.Media)),("$op",new string('a',32)));
        if(kind=="anchor")f.Db.Corrupt("DELETE FROM ordinary_publication_anchors;");
        if(kind=="schema")f.Db.Corrupt("DELETE FROM schema_migrations WHERE version=42;");
        if(kind=="missing")File.Move(f.Media,f.Media+".saved");
        try
        {
            using var a=f.Service(sid:kind=="sid"?"bad\0sid":UnifiedHistoryTestDatabase.Sid,binding:kind=="binding"?" ":UnifiedHistoryTestDatabase.Session,root:kind=="root"?f.Videos+".other":null);
            var session=a.Request(kind=="case"?"Candidate":f.Row.RecordingId);Assert.Equal(LocalRetentionState.Unavailable,(await session.Response.WaitAsync(TimeSpan.FromSeconds(5))).State);
            Assert.True(await session.Retirement.WaitAsync(TimeSpan.FromSeconds(5)));Assert.Equal(0,f.Adapter.Begins);Assert.Empty(f.Files.Api.Calls);
        }
        finally{if(kind=="missing")File.Move(f.Media+".saved",f.Media);}
    }

    [Theory]
    [InlineData("anchor")][InlineData("completed")][InlineData("reference")][InlineData("bundle")][InlineData("status")]
    public async Task FactsOrReferencesChangingAfterApprovalAreRecheckedInActualTransaction(string kind)
    {
        using var f=new LocalRetentionApprovalFixture();using var a=f.Service();var session=a.Request(f.Row.RecordingId);var permit=await f.Approve(a,session);
        if(kind=="anchor")f.Db.Corrupt("UPDATE ordinary_publication_anchors SET file_id=zeroblob(16);");
        if(kind=="completed")f.Db.Corrupt("UPDATE ordinary_recording_history SET completed_at_utc=completed_at_utc+1;");
        if(kind=="status")f.Db.Corrupt("UPDATE ordinary_recording_history SET status_code='failed';");
        if(kind=="bundle")f.Db.Corrupt("UPDATE ordinary_recording_bundles SET status_code='generating',bundle_path=$path,operation_id=$op;",("$path",AgentRecorder.Core.OrdinaryRecordingBundleOperation.DerivePath(f.Media)),("$op",new string('b',32)));
        if(kind=="reference")f.Db.Ordinary.Register(f.Db.Recording("new-reference",completed:true) with{OutputPath=f.Media});
        var calls=0;var result=await a.Execute(permit,f.Row.RecordingId,_=>{calls++;return LocalRetentionAttemptResult.Completed;});
        Assert.Equal(LocalRetentionState.ExecutionRejected,result.State);Assert.False(result.ApprovalConsumed);Assert.False(result.FileActionPerformed);Assert.Equal(0,calls);
        Assert.True(await session.Retirement.WaitAsync(TimeSpan.FromSeconds(5)));
    }
    private static void Set(object permit,string field,object value)=>typeof(LocalRetentionApproval.Permit).GetField(field,BindingFlags.Instance|BindingFlags.NonPublic)!.SetValue(permit,value);

    [Fact]
    public async Task UnknownOwnNestedRoleCannotOpenConfirmation()
    {
        using var f=new LocalRetentionApprovalFixture();f.Db.Corrupt("UPDATE ordinary_recording_history SET nested_role='damaged_role';");using var a=f.Service();var s=a.Request(f.Row.RecordingId);
        try
        {
            await Task.WhenAny(s.Response,f.Adapter.Entered.Task).WaitAsync(TimeSpan.FromSeconds(5));
            Assert.False(f.Adapter.Entered.Task.IsCompleted,"Damaged candidate facts must fail before local UI.");
            Assert.Equal(LocalRetentionState.Unavailable,(await s.Response.WaitAsync(TimeSpan.FromSeconds(5))).State);Assert.True(await s.Retirement.WaitAsync(TimeSpan.FromSeconds(5)));Assert.Empty(f.Files.Api.Calls);
        }
        finally{a.Dispose();f.Adapter.Decide(LocalRetentionUiChoice.Reject);f.Adapter.Retire.TrySetResult(true);await s.Retirement.WaitAsync(TimeSpan.FromSeconds(5));}
    }
}
