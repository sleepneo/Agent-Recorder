using AgentRecorder.Core;
using AgentRecorder.Infrastructure;
using AgentRecorder.Persistence;
using Xunit;
using Xunit.Abstractions;
using static AgentRecorder.Tests.OrdinaryArtifactHistoryTests;
using static AgentRecorder.Tests.UnifiedHistoryTestDatabase;

namespace AgentRecorder.Tests;

[Collection("NonParallel-AgentRecorderDataDir")]
public sealed class OrdinaryArtifactMetadataTests(ITestOutputHelper output)
{
    internal static OrdinaryArtifactFacts Facts(string path=@"C:\Task333\UserSelected\out.mp4",bool bundle=false)=>new(
        new OrdinaryRecordingHistorySnapshot("r",At,"completed","display","display_capture","Synthetic",1,"none",null,null,null,null,
            path,null,null,null,null,null,null,At.AddSeconds(1),At.AddSeconds(2),"duration_reached",12,1)
            {Bundle=bundle?new("ready",OrdinaryRecordingBundleOperation.DerivePath(path),null,new(BundleContentsGenerationTests.Items())):null},null);
    private static OrdinaryArtifactCheckService Service(OrdinaryArtifactFacts facts,Ancestors a,Leaf l)=>new(_=>facts,a,l,_=>true);

    [Theory]
    [InlineData(@"\\server\share\out.mp4")][InlineData(@"\\?\C:\x\out.mp4")][InlineData(@"\\.\C:\out.mp4")]
    [InlineData(@"C:\x\out.mp4:stream")][InlineData(@"x\out.mp4")][InlineData(@"C:\x\..\out.mp4")]
    [InlineData(@"C:/x/out.mp4")][InlineData(@"C:\CON\out.mp4")][InlineData(@"C:\COM1\out.mp4")]
    [InlineData(@"C:\x.\out.mp4")][InlineData(@"C:\x \out.mp4")]
    [InlineData("long")][InlineData("deep")]
    public async Task LexicalRefusalHasZeroAncestorAndLeafIo(string input)
    {
        var path=input=="long"?@"C:\"+new string('x',1020)+".mp4":input=="deep"?@"C:\"+string.Join("\\",Enumerable.Repeat("a",64))+@"\out.mp4":input;
        var a=new Ancestors();var l=new Leaf();var result=await Service(Facts(path),a,l).ReadAsync("r");
        Assert.True(result!.Media.Observation is "unsafe_path" or "unavailable");Assert.Null(result.Media.SampledAtUtc);Assert.Null(result.Media.ObservedSizeBytes);Assert.Empty(a.Calls);Assert.Empty(l.Calls);
    }
    [Fact]
    public async Task MappedOrUnsupportedDriveRefusesBeforeAttributes()
    {
        var a=new Ancestors();var l=new Leaf();var s=new OrdinaryArtifactCheckService(_=>Facts(),a,l,_=>false);var result=await s.ReadAsync("r");
        Assert.Equal("local_drive_unavailable",result!.Media.ReasonCode);Assert.Empty(a.Calls);Assert.Empty(l.Calls);
    }

    [Theory]
    [InlineData("reparse","unsafe_path","ancestor_reparse_point")][InlineData("file","unsafe_path","parent_not_directory")]
    [InlineData("missing","missing","parent_missing")][InlineData("denied","inaccessible","file_inaccessible")]
    [InlineData("sharing","inaccessible","file_inaccessible")][InlineData("unexpected","unavailable","metadata_unavailable")]
    public async Task AncestorFaultStopsBeforeLeaf(string fault,string observation,string reason)
    {
        var a=new Ancestors {Read=_=>fault switch {"reparse"=>FileAttributes.Directory|FileAttributes.ReparsePoint,"file"=>FileAttributes.Normal,
            "missing"=>throw new DirectoryNotFoundException("SECRET"),"denied"=>throw new UnauthorizedAccessException("SECRET"),
            "sharing"=>throw new IOException("SECRET",unchecked((int)0x80070020)),_=>throw new IOException("SECRET")}};
        var l=new Leaf();var result=await Service(Facts(),a,l).ReadAsync("r");Assert.Equal(observation,result!.Media.Observation);Assert.Equal(reason,result.Media.ReasonCode);Assert.Empty(l.Calls);
    }

    [Theory]
    [InlineData("missing","missing")][InlineData("denied","inaccessible")][InlineData("sharing","inaccessible")]
    [InlineData("unsupported","unavailable")][InlineData("reparse","unsafe_path")][InlineData("directory","unsafe_path")]
    [InlineData("changed","changed_during_probe")][InlineData("resolved","unsafe_path")][InlineData("identity_changed","changed_during_probe")]
    [InlineData("size_changed_during","changed_during_probe")][InlineData("invalid","unavailable")][InlineData("null","unavailable")]
    [InlineData("throw","unavailable")][InlineData("size","size_mismatch")][InlineData("normal","present_size_matches")]
    public async Task FileOutcomesRemainDistinctAndNeverVerified(string fault,string expected)
    {
        var a=new Ancestors();var l=new Leaf();l.Read=path=>
        {
            var good=Sample(path);return fault switch {
                "missing"=>new(FileObservationOutcome.Missing),"denied" or "sharing"=>new(FileObservationOutcome.Inaccessible),
                "unsupported"=>new(FileObservationOutcome.Unavailable),"reparse"=>new(FileObservationOutcome.ReparsePoint),
                "directory"=>new(FileObservationOutcome.Directory),"changed"=>new(FileObservationOutcome.MetadataChanged),
                "resolved"=>good with {AfterPath=@"C:\foreign.mp4"},
                "identity_changed"=>good with {After=good.After! with {Identity=new(4,5,6)}},
                "size_changed_during"=>good with {After=good.After! with {Length=13}},
                "invalid"=>good with {Before=good.Before! with {Links=0}},"null"=>null,
                "throw"=>throw new IOException("SECRET"),"size"=>Sample(path,13),_=>good};
        };
        var result=await Service(Facts(),a,l).ReadAsync("r");Assert.Equal(expected,result!.Media.Observation);Assert.NotNull(result.Media.SampledAtUtc);
        Assert.True(result.Media.IdentityState is "unverified" or "unknown");Assert.DoesNotContain("verified",result.Media.Observation);Assert.DoesNotContain("SECRET",result.Media.ReasonCode);
        Assert.Single(l.Calls);
    }

    [Theory]
    [InlineData(2,"missing")][InlineData(3,"missing")][InlineData(5,"inaccessible")][InlineData(32,"inaccessible")][InlineData(33,"inaccessible")][InlineData(50,"unavailable")]
    public async Task NativeAdapterErrorsUseOneHandleAndRelease(int error,string expected)
    {
        var api=new WindowsReadOnlyFileMetadataProbeTests.Api{Fail="open",Error=error};var a=new Ancestors();var service=new OrdinaryArtifactCheckService(_=>Facts(api.Path),a,new WindowsReadOnlyFileMetadataProbe(api),_=>true);
        var result=await service.ReadAsync("r");Assert.Equal(expected,result!.Media.Observation);Assert.Equal(1,api.OpenCount);Assert.True(api.Handles.Single().IsClosed);
    }

    [Fact]
    public async Task AtMostSixLeavesAndSixtyFourDistinctAncestorsWithPerRequestCache()
    {
        var path=@"C:\"+string.Join("\\",Enumerable.Repeat("a",62))+@"\out.mp4";var facts=Facts(path,bundle:true);var a=new Ancestors();var l=new Leaf();
        var s=Service(facts,a,l);var result=await s.ReadAsync("r");Assert.Equal(6,l.Calls.Count);Assert.Equal(64,a.Calls.Count);Assert.Equal(64,a.Calls.Distinct(StringComparer.OrdinalIgnoreCase).Count());
        Assert.Equal(5,result!.Bundle.Contents!.Count);await s.ReadAsync("r");Assert.Equal(128,a.Calls.Count);Assert.Equal(12,l.Calls.Count);
        var copy=new OrdinaryArtifactBundleCheck("ready",1,result.Bundle.Contents.ToArray());Assert.Throws<NotSupportedException>(()=>((IList<OrdinaryArtifactFileCheck>)copy.Contents!)[0]=copy.Contents[0]);
    }
    [Fact]
    public async Task CombinedAncestorBudgetFailsBeforeAnyIo()
    {
        var facts=Facts(@"C:\"+string.Join("\\",Enumerable.Repeat("a",63))+@"\out.mp4",bundle:true);
        var a=new Ancestors();var l=new Leaf();var result=await Service(facts,a,l).ReadAsync("r");Assert.Equal("ancestor_check_limit",result!.Media.ReasonCode);
        Assert.All(result.Bundle.Contents!,item=>Assert.Equal("ancestor_check_limit",item.ReasonCode));Assert.Empty(a.Calls);Assert.Empty(l.Calls);
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task TimeoutRetainsSingleHostLeaseAcrossServiceRecreationAndDbDispatch(bool db)
    {
        using var release=new ManualResetEventSlim();var entered=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var exited=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);var deadline=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var a=new Ancestors();var l=new Leaf();var reads=0;
        OrdinaryArtifactFacts? Read(string _){Interlocked.Increment(ref reads);if(db){entered.TrySetResult();Assert.True(release.Wait(TimeSpan.FromSeconds(5)));}return Facts(bundle:true);}
        if(!db)l.Read=path=>{if(l.Calls.Count==3){entered.TrySetResult();Assert.True(release.Wait(TimeSpan.FromSeconds(5)));}return Sample(path,path.EndsWith("metadata.json",StringComparison.Ordinal)?11:12);};
        var s=new OrdinaryArtifactCheckService(Read,a,l,_=>true){DeadlineForTests=token=>deadline.Task.WaitAsync(token),WorkerExitedForTests=()=>exited.TrySetResult()};
        var pending=s.ReadAsync("r");await entered.Task.WaitAsync(TimeSpan.FromSeconds(2));deadline.TrySetResult();var timeout=await pending;
        try
        {
            Assert.Equal("timeout",timeout!.ProbeState);Assert.True(timeout.Media.Observation is "not_checked" or "present_size_matches");
            if(!db){Assert.Equal("present_size_matches",timeout.Bundle.Contents![0].Observation);Assert.Equal("unavailable",timeout.Bundle.Contents[1].Observation);Assert.Equal("not_checked",timeout.Bundle.Contents[2].Observation);}
            for(var i=0;i<4;i++)Assert.Equal("busy",(await new OrdinaryArtifactCheckService(Read,a,l,_=>true).ReadAsync("r"))!.ProbeState);
            Assert.Equal(1,reads);Assert.Equal(db?0:3,l.Calls.Count);
        }
        finally{release.Set();await exited.Task.WaitAsync(TimeSpan.FromSeconds(2));}
        Assert.Equal(db?0:3,l.Calls.Count);Assert.Equal("timeout",timeout!.ProbeState);
        var next=await new OrdinaryArtifactCheckService(_=>Facts(),new Ancestors(),new Leaf(),_=>true).ReadAsync("r");Assert.Equal("complete",next!.ProbeState);
        output.WriteLine($"Controlled deadline: db={db}; native/DB worker retained host lease, 4 recreated services busy, no next file after cancellation; immutable partial result.");
    }

    [Fact]
    public async Task CancellationAndFailedDatabaseReleaseOnlyWhenWorkerActuallyExits()
    {
        using var cts=new CancellationTokenSource();using var release=new ManualResetEventSlim();var entered=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);var exited=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var a=new Ancestors();var l=new Leaf{Read=path=>{entered.TrySetResult();Assert.True(release.Wait(TimeSpan.FromSeconds(5)));return Sample(path);}};
        var s=new OrdinaryArtifactCheckService(_=>Facts(bundle:true),a,l,_=>true){WorkerExitedForTests=()=>exited.TrySetResult()};var pending=s.ReadAsync("r",cts.Token);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(2));cts.Cancel();var cancelled=await pending;Assert.Equal("cancelled",cancelled!.ProbeState);
        try{Assert.Equal("busy",(await s.ReadAsync("r"))!.ProbeState);}finally{release.Set();await exited.Task.WaitAsync(TimeSpan.FromSeconds(2));}
        Assert.Single(l.Calls);Assert.Equal("unavailable",cancelled.Media.Observation);Assert.Null(cancelled.Media.ObservedSizeBytes);
        var bad=new OrdinaryArtifactCheckService(_=>throw new OrdinaryRecordingHistoryUnavailableException("SECRET"),a,l,_=>true);
        await Assert.ThrowsAsync<OrdinaryRecordingHistoryUnavailableException>(()=>bad.ReadAsync("r"));Assert.Equal("complete",(await Service(Facts(),new(),new()).ReadAsync("r"))!.ProbeState);
        cts.Cancel();var count=l.Calls.Count;Assert.Equal("cancelled",(await s.ReadAsync("r",cts.Token))!.ProbeState);Assert.Equal(count,l.Calls.Count);
    }
}
