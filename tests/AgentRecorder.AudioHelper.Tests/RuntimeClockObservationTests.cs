using System.Text.Json;
using NAudio.Wave;
using Xunit;

namespace AgentRecorder.AudioHelper.Tests;

public class RuntimeClockObservationTests
{
    [Fact]
    public void DeviceRegressionBelowTrustedPositionKeepsTimelineFailureContract()
    {
        var t=Clock(); Put(t,960,10_000_000);
        Assert.Throws<LoopbackTimelineException>(()=>Put(t,0,10_100_000));
        Assert.Equal(3840,t.MediaBytes);
    }
    private static LoopbackTimeline Clock(long frequency = 10_000_000)
    {
        var t = new LoopbackTimeline(WaveFormat.CreateIeeeFloatWaveFormat(48000, 2), frequency, TimeSpan.FromMilliseconds(100))
        { ObserveRuntimeClock = true, ObservationMilliseconds = () => 0 };
        t.Start(frequency); return t;
    }
    private static LoopbackPacketAppendResult Put(LoopbackTimeline t, long d, long q, List<byte>? output = null, bool valid = true, bool flag = false)
        => t.AppendPacket(Enumerable.Repeat((byte)(d / 480 % 251), 3840).ToArray(), 3840, 480, d, q, valid,
            (b,o,n) => output?.AddRange(b.AsSpan(o,n).ToArray()), (b,n) => output?.AddRange(new byte[n]), flag);

    [Fact]
    public void Retained55PacketsReachBoundedObservationWithoutInventingFutureEvidence()
    {
        using var f = JsonDocument.Parse(RuntimeClockFixture.Json); var r = f.RootElement;
        var t = Clock(); t.ObserveRuntimeClock = true; // The red TRX retains the original-policy run.
        long seed = r.GetProperty("seed_qpc").GetInt64();
        // Seed at the original trusted pair; this is metadata and synthetic PCM only.
        t = new LoopbackTimeline(WaveFormat.CreateIeeeFloatWaveFormat(48000,2),10_000_000,TimeSpan.FromMilliseconds(100))
        { ObserveRuntimeClock = t.ObserveRuntimeClock, ObservationMilliseconds = () => 0 };
        t.Start(seed); Put(t, r.GetProperty("seed_device").GetInt64(), seed);
        foreach(var p in r.GetProperty("packets").EnumerateArray())
        {
            var result=t.AppendPacket(new byte[p[1].GetInt32()*8],p[1].GetInt32()*8,p[1].GetInt32(),p[2].GetInt64(),p[3].GetInt64(),p[4].GetBoolean(),(_,_,_)=>{},(_,_)=>{},p[5].GetBoolean());
            Assert.Equal(p[0].GetInt32()<7956 ? p[1].GetInt32()*8 : 0,result.PacketBytesWritten);
            Assert.Equal(0,result.ZeroBytesWritten); Assert.Equal(0,result.PacketBytesSkipped);
            Assert.Equal(p[0].GetInt32()>=7956,result.QpcOutlierAccepted);
        }
        Assert.Equal((480+25440)*8,t.MediaBytes);
        Assert.Equal(23679360,t.LastDeviceEnd); // No suspect PCM or gap padding committed yet.
        Assert.Equal(2,t.QpcOutlierCount); Assert.Equal(0,t.ConfirmedQpcPhaseRebaseCount);
        Assert.Equal(2,t.PendingObservationPackets);
        Assert.Throws<LoopbackTimelineException>(() => t.RequireObservationSettled());
    }

    [Fact]
    public void SyntheticReturnPreservesSamplesAndReportsTenMillisecondGapExactlyOnce()
    {
        var t=Clock(); var output=new List<byte>(); Put(t,0,10_000_000,output);
        Put(t,960,11_048_217,output); Put(t,1440,11_341_216,output);
        Assert.Equal(3840,output.Count); Assert.Equal(2,t.PendingObservationPackets);
        var result=Put(t,1920,10_400_000,output);
        Assert.Equal(3840,result.ZeroBytesWritten); Assert.Equal(3*3840,result.PacketBytesWritten);
        Assert.Equal(5*3840,output.Count); Assert.Equal(0,t.PendingObservationPackets);
        Assert.All(output.Skip(3840).Take(3840), b=>Assert.Equal(0,b));
        Assert.All(output.Skip(7680).Take(3840), b=>Assert.Equal(2,b));
        Assert.True(t.ContinuityDegraded);
    }

    [Theory]
    [InlineData(10_000_000)] [InlineData(3_579_545)]
    public void StableBoundedPhaseNeedsTwoMatchingIntervalsAndSpendsOriginalBudget(long frequency)
    {
        var t=Clock(frequency); Put(t,0,frequency);
        for(int i=1;i<=3;i++) Put(t,(i+1)*480,frequency+(long)(((i+1)*.01+.02)*frequency));
        Assert.Equal(0,t.PendingObservationPackets); Assert.Equal(1,t.ConfirmedQpcPhaseRebaseCount);
        Assert.InRange(t.CumulativeAbsoluteQpcPhaseAdjustmentTicks,frequency/50-2,frequency/50+2);
    }

    [Theory]
    [InlineData("timestamp")] [InlineData("flag")] [InlineData("qpc_back")]
    [InlineData("device_back")] [InlineData("gap")] [InlineData("duration")]
    [InlineData("frames")] [InlineData("phase")]
    public void InvalidOrUnboundedEvidenceFailsWithoutPublishingPendingPcm(string kind)
    {
        var t=Clock(); Put(t,0,10_000_000); Put(t,960,10_400_000);
        long before=t.MediaBytes;
        Assert.Throws<LoopbackTimelineException>(()=> {
            if(kind=="duration") t.ObservationMilliseconds=()=>151;
            if(kind=="frames") { for(int i=2;i<15;i++) Put(t,(i+1)*480,10_000_000+(i+1)*100_000+200_000+i*1000); return; }
            if(kind=="phase") { Put(t,1440,11_500_000); Put(t,1920,11_600_000); Put(t,2400,11_700_000); return; }
            Put(t,kind=="device_back"?0:kind=="gap"?10000:1440,kind=="qpc_back"?9_000_000:10_550_000,
                valid:kind!="timestamp",flag:kind=="flag");
        });
        Assert.Equal(before,t.MediaBytes);
    }

    [Fact]
    public void StopAndNoDataDeadlineCannotPadOverUnresolvedPcm()
    {
        var t=Clock(); Put(t,0,10_000_000); Put(t,960,10_400_000);
        Assert.Throws<LoopbackTimelineException>(()=>t.PadToTimestamp(20_000_000,(_,_)=>{},true));
        t.ObservationMilliseconds=()=>151;
        Assert.Throws<LoopbackTimelineException>(()=>t.PadToTimestamp(20_000_000,(_,_)=>{}));
        Assert.Equal(3840,t.MediaBytes);
    }

    [Fact]
    public void RepeatedOscillationCannotResetSessionObservationBudget()
    {
        var t=Clock(); long device=0; Put(t,device,10_000_000);
        for(int i=0;i<8;i++)
        {
            device+=481;
            Put(t,device,10_000_000+AudioPacketPositionMath.FramesToTimestampTicks(device,48000,10_000_000)+200_000);
            device+=480;
            Put(t,device,10_000_000+AudioPacketPositionMath.FramesToTimestampTicks(device,48000,10_000_000));
            Assert.Equal(0,t.PendingObservationPackets);
        }
        device+=481;
        Assert.Throws<LoopbackTimelineException>(()=>Put(t,device,10_000_000+AudioPacketPositionMath.FramesToTimestampTicks(device,48000,10_000_000)+200_000));
    }

    [Fact]
    public void LongDeviceTrajectoryNeverAccumulatesTimestampRoundingIntoSamples()
    {
        var t=Clock(3_579_545); var pcm=new byte[3840];
        for(int i=0;i<30_000;i++)
            t.AppendPacket(pcm,3840,480,i*480L,3_579_545+AudioPacketPositionMath.FramesToTimestampTicks(i*480L,48000,3_579_545),true,(_,_,_)=>{},(_,_)=>{});
        Assert.Equal(30_000L*3840,t.MediaBytes); Assert.Equal(0,t.PendingObservationPackets);
        Assert.Equal(0,t.CumulativeAbsoluteQpcPhaseAdjustmentTicks);
    }
}
