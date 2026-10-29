using System.Text.Json;
using System.Text.Json.Nodes;
using AgentRecorder.Capture;
using NAudio.Wave;
using Xunit;
namespace AgentRecorder.AudioHelper.Tests;
public class FaultIdleRetentionTests
{
    private static readonly WaveFormat Format=new(48000,32,2);
    private static void Packet(LoopbackIdleEvidence e,long seq,long ticks) => e.Packet(seq,ticks,ticks+1,seq*480,ticks*10000,0,1000);
    private static AudioPacketEventArgs Native(long seq,int offset=0) => new(new byte[8],8,1,seq*480+offset,1000,1000,true,
        observation:new AudioPacketObservation(seq,48000,8,480,seq*480,1000,0,100,101),chunkOffsetFrames:offset);
    private static LoopbackIdleEvidence Seed()
    {
        var e=new LoopbackIdleEvidence();Packet(e,1,1);e.Empty(10);Packet(e,2,210);e.Empty(1000);e.Empty(5999);Packet(e,30,6000);return e;
    }
    private static void Tail(LoopbackIdleEvidence e)
    {for(int i=0;i<50;i++){e.Empty(7000+i*10);Packet(e,40+i,7001+i*10);}}
    private static JsonElement Export(LoopbackPacketDiagnostics d)
    {string s=d.Export(1000);Assert.True(s.Length<=24000);Assert.True(LoopbackClockDiagnosticReader.TryRead(LoopbackPacketDiagnostics.Prefix+s,out var p));return p;}
    private static JsonElement Source(JsonElement p,long gen)=>p.GetProperty("idle_sources").EnumerateArray().Single(s=>s.GetProperty("generation").GetInt64()==gen).GetProperty("evidence");
    [Fact]
    public void FirstFaultAndItsLaterIdleSurviveFiftyShortWindowsThroughRealProducer()
    {
        var d=new LoopbackPacketDiagnostics();var e=Seed();d.RegisterIdleEvidence(7,e);
        long global=d.Observe(Native(30),7,Format,1,null);Assert.NotEqual(30,global);
        d.Finish(global,4,0,0,0,null,1);Tail(e);
        var p=Export(d);var ev=Source(p,7);
        Assert.Contains(ev.GetProperty("windows").EnumerateArray(),w=>w.GetProperty("start_ticks").GetInt64()==1000);
        Assert.Equal(global,p.GetProperty("fault_sequence").GetInt64());
        Assert.Equal(52,ev.GetProperty("total_windows").GetInt64());Assert.Equal(48,ev.GetProperty("omitted_windows").GetInt64());
    }
    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void FaultGenerationAndCapturedEvictedWindowSurviveLaterInputs(bool delayedFinish)
    {
        var d=new LoopbackPacketDiagnostics();d.RegisterIdleEvidence(1,new());var e=Seed();d.RegisterIdleEvidence(9,e);
        long fault=d.Observe(Native(30),9,Format,1,null);
        if(!delayedFinish)d.Finish(fault,4,0,0,0,null,1);
        Tail(e);for(int i=10;i<=18;i++)d.RegisterIdleEvidence(i,new());
        if(delayedFinish)d.Finish(fault,4,0,0,0,null,1);
        var p=Export(d);Assert.Equal(7,p.GetProperty("idle_sources_omitted").GetInt64());
        Assert.Contains(Source(p,9).GetProperty("windows").EnumerateArray(),w=>w.GetProperty("start_ticks").GetInt64()==1000);
        Assert.Equal(new long[]{9,30,1000,1},p.GetProperty("idle_fault_binding").EnumerateArray().Select(x=>x.GetInt64()));
    }
    [Theory]
    [InlineData(0)] [InlineData(160)]
    public void LaterCandidateAndNativeChunkBindByInputSequenceNotGlobalOrChunkDevice(int offset)
    {
        var d=new LoopbackPacketDiagnostics();var e=Seed();d.RegisterIdleEvidence(12,e);
        var first=d.Observe(Native(30),12,Format,1,null);d.Finish(first,2,0,0,0,null);
        Packet(e,31,6010);
        if(offset!=0){var chunk=d.Observe(Native(31),12,Format,1,null);d.Finish(chunk,2,0,0,0,null);}
        var failed=d.Observe(Native(31,offset),12,Format,1,null);d.Finish(failed,4,0,0,0,null,1);
        // A later different fault must never replace this one.
        var later=d.Observe(Native(999),12,Format,1,null);d.Finish(later,4,0,0,0,null,1);Tail(e);
        d.Finish(failed,3,8,0,0,null); // A revisit cannot erase the original rejection.
        var p=Export(d);Assert.Equal(failed,p.GetProperty("fault_sequence").GetInt64());
        Assert.Equal(new long[]{12,31,1000,1},p.GetProperty("idle_fault_binding").EnumerateArray().Select(x=>x.GetInt64()));
    }
    [Theory]
    [InlineData(0)] [InlineData(1)] [InlineData(2)]
    public void UnknownAssociationCannotPinAnUnrelatedRecentWindow(int kind)
    {
        var d=new LoopbackPacketDiagnostics();var e=Seed();if(kind!=1)d.RegisterIdleEvidence(5,e);else d.RegisterIdleEvidence(4,e);
        // Generation 4 really has native sequence 30; that must not satisfy a generation 5 fault.
        var packet=kind==2 ? new AudioPacketEventArgs(new byte[8],8,1,30*480,1000,1000,true) : Native(kind==1?30:999);
        var f=d.Observe(packet,5,Format,1,null);d.Finish(f,4,0,0,0,null,1);Tail(e);
        var p=Export(d);Assert.Equal(kind==2?3:kind==1?2:0,p.GetProperty("idle_fault_binding")[3].GetInt64());
        Assert.Equal(-1,p.GetProperty("idle_fault_binding")[2].GetInt64());
    }
    [Fact]
    public void OnlyAvailableFollowingPacketsAreKeptAndStopExportDoesNotWaitOrInventResume()
    {
        var d=new LoopbackPacketDiagnostics();var e=Seed();d.RegisterIdleEvidence(3,e);
        var f=d.Observe(Native(30),3,Format,1,null);d.Finish(f,4,0,0,0,null,1);
        e.Empty(6100);e.End(6200);
        var windows=Source(Export(d),3).GetProperty("windows");
        Assert.Single(windows.EnumerateArray().Single(w=>w.GetProperty("start_ticks").GetInt64()==1000).GetProperty("following").EnumerateArray());
        Assert.Equal(2,windows.EnumerateArray().Single(w=>w.GetProperty("start_ticks").GetInt64()==6100).GetProperty("end_reason").GetInt32());
    }
    [Fact]
    public void PinnedFaultCollectsFourActualPacketsEvenWhenAnotherLongWindowTakesTheNormalCursor()
    {
        var d=new LoopbackPacketDiagnostics();var e=Seed();d.RegisterIdleEvidence(3,e);
        var f=d.Observe(Native(30),3,Format,1,null);d.Finish(f,4,0,0,0,null,1);
        e.Empty(7000);Packet(e,50,12000);Packet(e,51,12010);Packet(e,52,12020);Packet(e,53,12030);
        var window=Source(Export(d),3).GetProperty("windows").EnumerateArray().Single(w=>w.GetProperty("start_ticks").GetInt64()==1000);
        Assert.Equal(new long[]{30,50,51,52},window.GetProperty("following").EnumerateArray().Select(p=>p[0].GetInt64()));
    }
    [Fact]
    public async Task PinStopAndExportHaveNoReverseLockOrMutableSnapshots()
    {
        var d=new LoopbackPacketDiagnostics();var e=Seed();d.RegisterIdleEvidence(3,e);
        var f=d.Observe(Native(30),3,Format,1,null);d.Finish(f,4,0,0,0,null,1);
        var tasks=new[]{Task.Run(()=>{for(int i=0;i<500;i++){e.Empty(7000+i*10);Packet(e,40+i,7001+i*10);}e.End(13000);}),
            Task.Run(()=>{for(int i=0;i<100;i++)Export(d);}),Task.Run(()=>{for(int i=0;i<100;i++)d.Finish(f,4,0,0,0,null,1);})};
        await Task.WhenAll(tasks).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(1000,Export(d).GetProperty("idle_fault_binding")[2].GetInt64());
    }
    [Fact]
    public void FullBudgetKeepsFaultAndNewestPacketAndAcceptsBothOldSchemas()
    {
        var d=new LoopbackPacketDiagnostics();var e=Seed();d.RegisterIdleEvidence(2,e);
        var f=d.Observe(Native(30),2,Format,1,new long[17]);d.Finish(f,4,0,0,0,new long[17],1);Tail(e);
        long last=0;for(int i=0;i<2000;i++){last=d.Observe(Native(1000+i),2,Format,1,Enumerable.Repeat(long.MaxValue,17).ToArray());d.Finish(last,3,8,0,0,Enumerable.Repeat(long.MaxValue,17).ToArray());}
        for(int i=3;i<=9;i++)d.RegisterIdleEvidence(i,Seed());
        var p=Export(d);Assert.Contains(p.GetProperty("packets").EnumerateArray(),r=>r[0].GetInt64()==f);
        Assert.Equal(last,p.GetProperty("packets").EnumerateArray().Last()[0].GetInt64());
        Assert.Equal(2,p.GetProperty("idle_fault_binding")[0].GetInt64());
        var legacy=JsonNode.Parse(p.GetRawText())!;legacy.AsObject().Remove("idle_policy");legacy.AsObject().Remove("idle_fault_binding");legacy["idle_evidence_version"]=1;
        Assert.True(LoopbackClockDiagnosticReader.TryRead(LoopbackPacketDiagnostics.Prefix+legacy.ToJsonString(),out _));
        foreach(var key in new[]{"idle_evidence_version","idle_sources_omitted","idle_sources"})legacy.AsObject().Remove(key);
        Assert.True(LoopbackClockDiagnosticReader.TryRead(LoopbackPacketDiagnostics.Prefix+legacy.ToJsonString(),out _));
        var bad=JsonNode.Parse(p.GetRawText())!;bad["idle_fault_binding"]![1]=999;
        Assert.False(LoopbackClockDiagnosticReader.TryRead(LoopbackPacketDiagnostics.Prefix+bad.ToJsonString(),out _));
        bad=JsonNode.Parse(p.GetRawText())!;bad["pcm"]="secret";
        Assert.False(LoopbackClockDiagnosticReader.TryRead(LoopbackPacketDiagnostics.Prefix+bad.ToJsonString(),out _));
    }
}
