using System.Reflection;
using AgentRecorder.Core;
using AgentRecorder.Infrastructure;
using AgentRecorder.Persistence;
using Xunit;

namespace AgentRecorder.Tests;

[Collection("NonParallel-AgentRecorderDataDir")]
public sealed class LocalRetentionDeletionContractTests
{
    [Fact]
    public async Task ConsumedAttemptRequestsExactlyOneOriginalHandleAndOldExecuteNeverDoes()
    {
        using var f=new LocalRetentionDeletionFixture();using var a=f.Service();var s=a.Request(f.Row.RecordingId);var p=await f.Approve(a,s);
        Assert.Equal(0,f.Native.Calls);
        f.Native.BeforeRequest=h=>
        {
            Assert.True(s.Current.ApprovalConsumed);Assert.Equal(LocalRetentionState.Consumed,s.Current.State);f.AssertProtected();
            Assert.True(a.RequestDeletion(p,f.Row.RecordingId).Response.IsCompletedSuccessfully);
        };
        var op=a.RequestDeletion(p,f.Row.RecordingId);var r=await LocalRetentionDeletionFixture.Finish(op,s);
        Assert.Equal(LocalDeletionProgress.DispositionAccepted,r.Progress);Assert.True(r.FileActionPerformed);Assert.True(r.ApprovalConsumed);
        Assert.Equal(p.ApprovalId,r.ApprovalId);Assert.False(string.IsNullOrWhiteSpace(r.AttemptId));Assert.Equal(1,f.Native.Calls);
        Assert.Same(f.Files.Api.Opened.Single(n=>n.Leaf).Handle,Assert.Single(f.Native.Handles));
        OriginalFileScopeFixture.AssertClosedInReverse(f.Files.Api);
        Assert.Equal(LocalDeletionProgress.NotAttempted,(await a.RequestDeletion(p,f.Row.RecordingId).Response).Progress);Assert.Equal(1,f.Native.Calls);
    }

    [Theory]
    [InlineData("null")][InlineData("id")][InlineData("host")][InlineData("expired")][InlineData("cancel")][InlineData("consumed")]
    public async Task UnusablePermitHasZeroDisposition(string kind)
    {
        using var f=new LocalRetentionDeletionFixture();using var a=f.Service();using var other=f.Service();var s=a.Request(f.Row.RecordingId);var p=await f.Approve(a,s);
        if(kind=="expired")f.Clock.Advance(60);if(kind=="cancel")s.Dispose();
        if(kind=="consumed")
        {
            var old=await a.Execute(p,f.Row.RecordingId,c=>{c.RequireActive();return LocalRetentionAttemptResult.Completed;});
            Assert.True(old.ApprovalConsumed);Assert.False(old.FileActionPerformed);
        }
        var op=(kind=="host"?other:a).RequestDeletion(kind=="null"?null:p,kind=="id"?"Candidate":f.Row.RecordingId);
        var r=await op.Response;Assert.Equal(LocalDeletionProgress.NotAttempted,r.Progress);Assert.False(r.FileActionPerformed);Assert.Equal(0,f.Native.Calls);
        s.Dispose();Assert.True(await s.Retirement.WaitAsync(TimeSpan.FromSeconds(6)));
    }

    [Theory]
    [InlineData("id")][InlineData("path")][InlineData("size")][InlineData("volume")][InlineData("low")][InlineData("high")][InlineData("completed")][InlineData("cutoff")]
    [InlineData("sid")][InlineData("session")][InlineData("root")][InlineData("store")][InlineData("writer")][InlineData("engine")][InlineData("lifetime")][InlineData("semantics")][InlineData("intent")]
    public async Task WrongFrozenMaterialCannotDispatchDeletion(string kind)
    {
        using var f=new LocalRetentionDeletionFixture();using var a=f.Service();var s=a.Request(f.Row.RecordingId);var p=await f.Approve(a,s);
        var facts=f.Ui.Intent.Facts;var anchor=facts.Anchor;
        object material=kind switch
        {
            "id"=>facts with{RecordingId="other"},"path"=>facts with{Anchor=anchor with{Path=anchor.Path+".mp4"}},"size"=>facts with{Anchor=anchor with{Length=13}},
            "volume"=>facts with{Anchor=anchor with{Identity=anchor.Identity with{VolumeSerialNumber=999}}},
            "low"=>facts with{Anchor=anchor with{Identity=anchor.Identity with{IdLow=999}}},"high"=>facts with{Anchor=anchor with{Identity=anchor.Identity with{IdHigh=999}}},
            "completed"=>facts with{CompletedAtUtc=facts.CompletedAtUtc.AddTicks(1)},"cutoff"=>facts with{Cutoff=facts.Cutoff.AddTicks(1)},
            "lifetime"=>Guid.NewGuid(),"intent"=>LocalRetentionIntent.ForConfirmedFacts(facts,f.Clock),"store" or "writer" or "engine"=>new object(),_=>"different"
        };
        var field=kind is "id" or "path" or "size" or "volume" or "low" or "high" or "completed" or "cutoff"?"facts":kind=="session"?"binding":kind;
        typeof(LocalRetentionApproval.Permit).GetField("_"+field,BindingFlags.Instance|BindingFlags.NonPublic)!.SetValue(p,material);
        Assert.Equal(LocalDeletionProgress.NotAttempted,(await a.RequestDeletion(p,f.Row.RecordingId).Response).Progress);
        Assert.Equal(0,f.Native.Calls);Assert.Empty(f.Files.Api.Opened);s.Dispose();Assert.True(await s.Retirement.WaitAsync(TimeSpan.FromSeconds(6)));
    }

    [Theory]
    [InlineData("anchor")][InlineData("completed")][InlineData("status")][InlineData("bundle")][InlineData("reference")][InlineData("unknown")][InlineData("schema")]
    public async Task ActualTransactionRejectsChangedFactsAndFiniteRelations(string kind)
    {
        using var f=new LocalRetentionDeletionFixture();using var a=f.Service();var s=a.Request(f.Row.RecordingId);var p=await f.Approve(a,s);
        if(kind=="anchor")f.Db.Corrupt("UPDATE ordinary_publication_anchors SET file_id=zeroblob(16);");
        if(kind=="completed")f.Db.Corrupt("UPDATE ordinary_recording_history SET completed_at_utc=completed_at_utc+1;");
        if(kind=="status")f.Db.Corrupt("UPDATE ordinary_recording_history SET status_code='failed';");
        if(kind=="unknown")f.Db.Corrupt("UPDATE ordinary_recording_history SET nested_role='unknown_role';");
        if(kind=="schema")f.Db.Corrupt("DELETE FROM schema_migrations WHERE version=42;");
        if(kind=="bundle")f.Db.Corrupt("UPDATE ordinary_recording_bundles SET status_code='generating',bundle_path=$p,operation_id=$o;",("$p",OrdinaryRecordingBundleOperation.DerivePath(f.Media)),("$o",new string('a',32)));
        if(kind=="reference")f.Db.Ordinary.Register(f.Db.Recording("other",completed:true) with{OutputPath=f.Media});
        var r=await LocalRetentionDeletionFixture.Finish(a.RequestDeletion(p,f.Row.RecordingId),s);
        Assert.Equal(LocalDeletionProgress.NotAttempted,r.Progress);Assert.False(r.FileActionPerformed);Assert.False(r.ApprovalConsumed);Assert.Equal(0,f.Native.Calls);
    }

    [Theory]
    [InlineData("identity")][InlineData("length")][InlineData("links")][InlineData("reparse")][InlineData("directory")][InlineData("pending")][InlineData("path")][InlineData("ancestor")][InlineData("metadata")]
    public async Task FreshRecheckImmediatelyBeforeNativeRequestRejectsChangedOriginal(string kind)
    {
        using var f=new LocalRetentionDeletionFixture();using var a=f.Service();var s=a.Request(f.Row.RecordingId);var p=await f.Approve(a,s);
        f.Files.Api.StandardValue=(node,n,v)=>node.Leaf&&n>=5?kind switch
        { "length"=>v with{EndOfFile=13},"links"=>v with{NumberOfLinks=2},"directory"=>v with{Directory=1},"pending"=>v with{DeletePending=1},_=>v }:v;
        f.Files.Api.IdentityValue=(node,n,v)=>node.Leaf&&n>=5&&kind=="identity"?v with{IdHigh=999}:v;
        f.Files.Api.BasicValue=(node,n,v)=>node.Leaf&&n>=5&&kind=="reparse"?v with{Attributes=(uint)FileAttributes.ReparsePoint}:v;
        f.Files.Api.PathValue=(node,n,v)=>node.Leaf&&n>=5&&kind=="path"?v+".other":v;
        f.Files.Api.Fail=c=>kind=="metadata"&&c.Stage=="basic"&&f.Files.Api.Opened.Any(n=>n.Leaf&&n.Index==c.Index)&&c.Number>=5;
        if(kind=="ancestor")f.Files.Api.Before=c=>{if(c.Stage=="basic"&&c.Number==5&&f.Files.Api.Opened.Any(n=>n.Leaf&&n.Index==c.Index))f.Files.Api.IdentityValue=(node,n,v)=>!node.Leaf?v with{IdHigh=999}:v;};
        var r=await LocalRetentionDeletionFixture.Finish(a.RequestDeletion(p,f.Row.RecordingId),s);
        Assert.True(r.ApprovalConsumed);Assert.Equal(LocalDeletionProgress.NotAttempted,r.Progress);Assert.Equal(0,f.Native.Calls);
    }

    [Theory]
    [InlineData(5)][InlineData(32)][InlineData(50)][InlineData(1224)]
    public async Task NativeRejectionKeepsExactErrorAndNeverRetries(int error)
    {
        using var f=new LocalRetentionDeletionFixture();f.Native.FakeRequest=()=>new(OriginalFileDispositionOutcome.Rejected,error);
        using var a=f.Service();var s=a.Request(f.Row.RecordingId);var p=await f.Approve(a,s);var r=await LocalRetentionDeletionFixture.Finish(a.RequestDeletion(p,f.Row.RecordingId),s);
        Assert.Equal(LocalDeletionProgress.DispositionRejected,r.Progress);Assert.Equal(error,r.Win32Error);Assert.False(r.FileActionPerformed);Assert.True(r.ApprovalConsumed);Assert.Equal(1,f.Native.Calls);
    }

    [Fact]
    public async Task UnsupportedPlatformNeverGetsOriginalLeafOrSubmits()
    {
        using var f=new LocalRetentionDeletionFixture();using var a=f.Service();var s=a.Request(f.Row.RecordingId);var p=await f.Approve(a,s);f.Files.Api.Platform=false;
        var r=await LocalRetentionDeletionFixture.Finish(a.RequestDeletion(p,f.Row.RecordingId),s);Assert.False(r.ApprovalConsumed);Assert.Equal(0,f.Native.Calls);Assert.Empty(f.Files.Api.Opened);
    }

    [Fact]
    public void CapabilityConcreteConstructionAndEntryDoNotExposeGenericDeleteOrCallerCallback()
    {
        var concrete=typeof(LocalRetentionApproval.DeletionOperation).GetNestedType("Authorization",BindingFlags.NonPublic)!;
        Assert.True(concrete.IsSealed);Assert.True(concrete.IsNestedPrivate);
        Assert.All(concrete.GetConstructors(BindingFlags.Public|BindingFlags.NonPublic|BindingFlags.Instance),c=>Assert.True(c.IsPrivate));
        Assert.DoesNotContain(typeof(OriginalFileBorrowContext).GetMethods(BindingFlags.Public|BindingFlags.NonPublic|BindingFlags.Instance),m=>m.Name.Contains("Disposition")||m.Name.Contains("Delete"));
        var method=typeof(LocalRetentionApproval).GetMethod("RequestDeletion",BindingFlags.Instance|BindingFlags.NonPublic)!;
        Assert.Equal(new[]{typeof(LocalRetentionApproval.Permit),typeof(string)},method.GetParameters().Select(p=>p.ParameterType));
    }
}
