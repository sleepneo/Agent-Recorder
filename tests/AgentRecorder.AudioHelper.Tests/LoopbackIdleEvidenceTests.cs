using System.Text.Json;
using System.Text.Json.Nodes;
using AgentRecorder.Capture;
using NAudio.Wave;
using Xunit;
namespace AgentRecorder.AudioHelper.Tests;

public class LoopbackIdleEvidenceTests
{
    private static JsonElement Snapshot(LoopbackIdleEvidence e) => JsonSerializer.SerializeToElement(e.Snapshot());
    [Theory]
    [InlineData(100)] [InlineData(500)] [InlineData(1000)] [InlineData(3000)] [InlineData(5000)]
    public void NonDeliveryWindowIsMeasuredWithoutInventingSilence(int ms)
    {
        var e=new LoopbackIdleEvidence();
        e.Packet(1,1,2,0,100,0,1000); e.Empty(10); e.Empty(10+ms-1);
        e.Packet(2,10+ms,11+ms,480,100+ms*10000L,0,1000);
        for(int i=3;i<7;i++) { e.Empty(12+ms+i); e.Packet(i,20+ms+i,21+ms+i,i*480,100+i,2,1000); }
        var windows=Snapshot(e).GetProperty("windows");
        var w=windows.EnumerateArray().Single(x=>x.GetProperty("start_ticks").GetInt64()==10);
        Assert.Equal(ms,w.GetProperty("end_ticks").GetInt64()-10);
        Assert.Equal(2,w.GetProperty("empty_polls").GetInt64());
        Assert.Equal(1,w.GetProperty("previous")[0].GetInt64());
        Assert.Equal(4,w.GetProperty("following").GetArrayLength());
        Assert.Equal(2,w.GetProperty("following")[0][0].GetInt64());
        Assert.Equal(0,w.GetProperty("following")[0][5].GetInt64());
    }
    [Fact]
    public void NoResumeStopAndFreshGenerationCannotManufactureAResume()
    {
        var e=new LoopbackIdleEvidence(); e.Empty(100); e.Empty(500); e.End(600); e.End(700);
        var w=Snapshot(e).GetProperty("windows")[0];
        Assert.Equal(2,w.GetProperty("end_reason").GetInt32()); Assert.Equal(600,w.GetProperty("end_ticks").GetInt64());
        Assert.Empty(w.GetProperty("following").EnumerateArray());
        Assert.Equal(0,Snapshot(new()).GetProperty("total_windows").GetInt64());
    }
    [Theory]
    [InlineData(100)] [InlineData(500)] [InlineData(1000)] [InlineData(3000)] [InlineData(5000)]
    public void ContinuousZeroSamplesAndPositionContinuousResumeKeepExactSentinelMediaMap(int ms)
    {
        var t=new LoopbackTimeline(new WaveFormat(48000,16,1),10_000_000,TimeSpan.FromMilliseconds(100)){ObserveRuntimeClock=true};t.Start(10_000_000);
        var output=new List<byte>();
        void Put(byte value,int frame,long d) => t.AppendPacket(Enumerable.Repeat(value,frame*2).ToArray(),frame*2,frame,d,
            10_000_000+AudioPacketPositionMath.FramesToTimestampTicks(d,48000,10_000_000),true,(b,o,n)=>output.AddRange(b.AsSpan(o,n).ToArray()),(_,_)=>throw new Exception("No inferred gap permitted"));
        Put(0x11,480,0); Put(0,48*ms,480); Put(0x22,480,480+48*ms);
        Assert.Equal((960+48*ms)*2,output.Count);
        Assert.All(output.Take(960),b=>Assert.Equal(0x11,b)); Assert.All(output.Skip(960).Take(ms*96),b=>Assert.Equal(0,b));
        Assert.All(output.TakeLast(960),b=>Assert.Equal(0x22,b)); Assert.False(t.ContinuityDegraded);
    }
    [Fact]
    public void WorstCaseBoundsRemainReadableAndRejectUnexpectedMetadata()
    {
        var d=new LoopbackPacketDiagnostics();
        for(int source=1;source<=8;source++)
        {
            var e=new LoopbackIdleEvidence();
            for(int n=0;n<100;n++) {e.Empty(n*1000000L+1);e.Empty(n*1000000L+100);for(int k=0;k<4;k++)e.Packet(n*4+k+1,n*1000000L+1000+k,n*1000000L+1001+k,n*480,10000+n,0,1000);}
            d.RegisterIdleEvidence(source,e);
        }
        string json=d.Export(10_000_000); Assert.True(json.Length<=24000);
        Assert.True(LoopbackClockDiagnosticReader.TryRead(LoopbackPacketDiagnostics.Prefix+json,out var payload));
        Assert.Equal(4,payload.GetProperty("idle_sources").GetArrayLength());
        Assert.Equal(4,payload.GetProperty("idle_sources_omitted").GetInt64());
        var bad=JsonNode.Parse(json)!;bad["idle_sources"]![0]!["evidence"]!["windows"]![0]!["device_id"]="private";
        Assert.False(LoopbackClockDiagnosticReader.TryRead(LoopbackPacketDiagnostics.Prefix+bad.ToJsonString(),out _));
        Assert.DoesNotContain("private",json);
    }
}
