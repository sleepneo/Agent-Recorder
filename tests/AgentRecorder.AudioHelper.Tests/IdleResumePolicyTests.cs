using NAudio.Wave;
using AgentRecorder.Capture;
using System.Text.Json.Nodes;
using Xunit;
namespace AgentRecorder.AudioHelper.Tests;
public class IdleResumePolicyTests
{
    private sealed class Run
    {
        internal readonly LoopbackIdleEvidence Source=new();
        internal readonly LoopbackTimeline Timeline;
        internal readonly LoopbackIdleResumePolicy Policy;
        internal readonly List<byte> Bytes=new();
        internal long Device=207954720,Qpc=5836883264528; internal long Now;
        internal Run(bool recorded3893=false)
        {
            if(recorded3893){Device=225144480;Qpc=5980875862542;}
            var f=new WaveFormat(48000,32,2);Timeline=new(f,10_000_000,TimeSpan.FromMilliseconds(100)){ObserveRuntimeClock=true};Timeline.Start(Qpc-100000);
            Policy=new(Timeline,f,10_000_000){Milliseconds=()=>Now};
            Put(Packet(recorded3893?3891:3518,Device-480,Qpc-100000,0,0x11));
            if(recorded3893)
            {
                var o=new AudioPacketObservation(3892,48000,8,480,Device,Qpc,2,5980876125766,5980876125768,4800,50){InputIdentity=Source};
                o.CompleteRelease(5980876125769,5980876125770,true);
                Put(new(new byte[3840],3840,480,Device,Qpc,Qpc,true,observation:o){DiagnosticReceivedTicks=5980876125772});
            }
            else Put(Packet(3519,Device,Qpc,2,0));
        }
        internal AudioPacketEventArgs Packet(long seq,long d,long q,int flags=0,byte value=0x22,LoopbackIdleTicket? ticket=null,LoopbackIdleEvidence? source=null)
        {
            var o=new AudioPacketObservation(seq,48000,8,480,d,q,flags,q+50000,q+50010,4800,50){InputIdentity=source??Source,IdleBefore=ticket};o.CompleteRelease(q+50020,q+50030,true);
            return new(Enumerable.Repeat(value,3840).ToArray(),3840,480,d,q,q,(flags&4)==0,(flags&1)!=0,o);
        }
        internal LoopbackIdleTicket Ticket(long ms=5000,long maxPoll=700000)=>new(Source,Qpc+50100,Qpc+ms*10000+150000-maxPoll,Qpc+ms*10000+150000,Math.Max(2,ms/60),maxPoll,3519,Qpc+50030,Device,480,Qpc,2);
        internal LoopbackPacketAppendResult Put(AudioPacketEventArgs p,long gen=1)=>Policy.Append(p,gen,(b,o,n)=>Bytes.AddRange(b.AsSpan(o,n).ToArray()),(b,n)=>Bytes.AddRange(new byte[n]));
    }
    private static Run RecordedCandidate()
    {
        var r=new Run(true);
        // Actual raw native metadata; all PCM (including prefix) is synthetic.
        var ticket=new LoopbackIdleTicket(r.Source,5980876126124,5980877360980,5980877976494,3,625712,
            3892,5980876125770,225144480,480,5980875862542,2);
        AudioPacketEventArgs Native(long seq,long d,long q,long get,long getEnd,long releaseStart,long release,long received,byte value,LoopbackIdleTicket? idle=null)
        {
            var o=new AudioPacketObservation(seq,48000,8,480,d,q,0,get,getEnd,4800,50){InputIdentity=r.Source,IdleBefore=idle};o.CompleteRelease(releaseStart,release,true);
            return new(Enumerable.Repeat(value,3840).ToArray(),3840,480,d,q,q,true,observation:o){DiagnosticReceivedTicks=received};
        }
        r.Put(Native(3893,225145440,5980877930419,5980877976494,5980877976524,5980877976583,5980877976592,5980877976642,0x22,ticket));
        Assert.Equal(1,r.Policy.Pending);Assert.Equal(7680,r.Bytes.Count);
        r.Now=61; // Measured callback delta is 61.1815ms; not the QPC media timestamp delta.
        r.Put(Native(3894,225145920,5980878123302,5980878588354,5980878588378,5980878588399,5980878588405,5980878588457,0x33));
        return r;
    }
    [Fact]
    public void Recorded3893And3894AwaitBoundedTrajectoryInsteadOfRejecting()
    {
        var r=RecordedCandidate();Assert.Equal(2,r.Policy.Pending);Assert.Equal(7680,r.Bytes.Count);
        Assert.Equal(92883,r.Policy.Snapshot()[9]);Assert.Equal(1850370,r.Policy.Snapshot()[5]);
        Assert.Equal(0,r.Policy.Snapshot()[0]);var error=Assert.Throws<LoopbackTimelineException>(()=>r.Policy.RequireSettled());
        Assert.Contains("idle_candidate_first_native=3893",error.Message);Assert.Contains("idle_candidate_window_start=5980876126124",error.Message);
    }
    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void RecordedPrefixWithExplicitSyntheticConvergenceDeliversExactPcmAndMedia(bool newPhase)
    {
        var r=RecordedCandidate();long offset=newPhase?92883:0;int end=newPhase?4:5;
        LoopbackPacketAppendResult result=default;
        for(int i=2;i<end;i++)
        {
            // No real 3895+ exists: these are synthetic alternative hypotheses, not a recording claim.
            r.Now=i*63; // Synthetic successors delivered at the measured poll cadence, including beyond 150ms.
            long device=225145440+i*480,qpc=5980877930419+i*100000+offset,get=5980877976494+r.Now*10000;
            var native=new AudioPacketObservation(3893+i,48000,8,480,device,qpc,0,get,get+10,4800,50){InputIdentity=r.Source};native.CompleteRelease(get+20,get+30,true);
            var packet=new AudioPacketEventArgs(Enumerable.Repeat((byte)(0x22+i*0x11),3840).ToArray(),3840,480,device,qpc,qpc,true,observation:native){DiagnosticReceivedTicks=get+40};
            result=r.Put(packet);
            if(i<end-1){Assert.Equal(0,result.TotalBytesWritten);Assert.Equal(7680,r.Bytes.Count);}
        }
        Assert.Equal(0,r.Policy.Pending);Assert.Equal(1,r.Policy.Snapshot()[0]);Assert.Equal(1867877,r.Policy.Snapshot()[2]);
        Assert.Equal(71720,result.NormalIdleZeroBytes);Assert.Equal(75560,result.ZeroBytesWritten);
        Assert.Equal(end*3840,result.PacketBytesWritten);Assert.Equal(0,result.PacketBytesSkipped);
        Assert.Equal(83240+end*3840,r.Bytes.Count);Assert.Equal(r.Bytes.Count,r.Timeline.MediaBytes);
        Assert.All(r.Bytes.Take(3840),b=>Assert.Equal(0x11,b));Assert.All(r.Bytes.Skip(3840).Take(79400),b=>Assert.Equal(0,b));
        for(int i=0;i<end;i++)Assert.All(r.Bytes.Skip(83240+i*3840).Take(3840),b=>Assert.Equal((byte)(0x22+i*0x11),b));
        Assert.Equal(newPhase?1:0,r.Timeline.ConfirmedQpcPhaseRebaseCount);
        Assert.Equal(newPhase?92883:0,r.Timeline.CumulativeAbsoluteQpcPhaseAdjustmentTicks);
        Assert.True(r.Timeline.ContinuityDegraded);Assert.True(r.Timeline.QpcOutlierCount>0);
        // The offset spends phase budget, never extra media samples. Afterwards original guards resume.
        var next=r.Packet(3893+end,225145440+end*480,5980877930419+end*100000+offset,value:0x77);
        Assert.Equal(3840,r.Put(next).PacketBytesWritten);r.Policy.RequireSettled();
        Assert.Equal((83240+(end+1)*3840)/384000d,r.Timeline.MediaBytes/384000d);
        Assert.InRange(Math.Abs(offset/10000d),0,20); // Maximum added QPC/media discrepancy, no duration extension.
    }
    [Theory]
    [InlineData("drift")] [InlineData("oscillate")] [InlineData("device_back")] [InlineData("gap")]
    [InlineData("qpc_back")] [InlineData("source")] [InlineData("generation")] [InlineData("sequence")]
    [InlineData("deadline")] [InlineData("no_more")] [InlineData("handoff")]
    public void RecordedPrefixWithSyntheticBadOrAbsentSuccessorsNeverWritesHeldPcm(string kind)
    {
        var r=RecordedCandidate();
        if(kind=="no_more"){r.Now=301;Assert.Throws<LoopbackTimelineException>(()=>r.Policy.CheckDeadline());}
        else if(kind=="handoff")Assert.Throws<LoopbackTimelineException>(()=>r.Policy.CommitEpoch(2,new()));
        else if(kind is "drift" or "oscillate")
        {
            Assert.Throws<LoopbackTimelineException>(()=>
            {
                for(int i=2;i<8;i++)r.Put(r.Packet(3893+i,225145440+i*480,5980877930419+i*100000+
                    (kind=="drift"?i*92883:(i%2)*92883)));
            });
        }
        else
        {
            if(kind=="deadline")r.Now=301;
            Assert.Throws<LoopbackTimelineException>(()=>r.Put(r.Packet(kind=="sequence"?3999:3895,
                kind=="device_back"?225145440:kind=="gap"?225146880:225146400,
                kind=="qpc_back"?5980878123301:5980878223302,source:kind=="source"?new():null),kind=="generation"?2:1));
        }
        Assert.Equal(7680,r.Bytes.Count);Assert.Equal(0,r.Policy.Snapshot()[0]);Assert.True(r.Policy.Pending>0);
        Assert.Throws<LoopbackTimelineException>(()=>r.Policy.RequireSettled());
    }
    [Theory]
    [InlineData(100)] [InlineData(500)] [InlineData(1000)] [InlineData(3000)] [InlineData(5000)]
    public void SlowSettlingAcrossIdleLengthsKeepsSamplesAndSpendsOnlyActualPhase(int ms)
    {
        var r=new Run();long q=r.Qpc+ms*10000L+100000;
        r.Put(r.Packet(3520,r.Device+480,q,ticket:r.Ticket(ms,Math.Min(700000,ms*5000))));
        LoopbackPacketAppendResult result=default;
        for(int i=1;i<=3;i++)result=r.Put(r.Packet(3520+i,r.Device+480+i*480,q+i*100000+92883,value:(byte)(0x22+i*0x11)));
        Assert.Equal(ms*384L,result.NormalIdleZeroBytes);Assert.Equal(4*3840,result.PacketBytesWritten);
        Assert.Equal(7680+ms*384L+4*3840,r.Bytes.Count);Assert.Equal(92883,r.Timeline.CumulativeAbsoluteQpcPhaseAdjustmentTicks);
        Assert.Equal(1,r.Timeline.ConfirmedQpcPhaseRebaseCount);Assert.Equal(0,r.Policy.Pending);
    }
    [Fact]
    public void SlowPendingHonorsDeadlineBoundaryAndDoesNotRefreshItPerPacket()
    {
        var r=RecordedCandidate();r.Now=299;r.Put(r.Packet(3895,225146400,5980878223302));
        r.Now=300;r.Policy.CheckDeadline();Assert.Equal(3,r.Policy.Pending);
        r.Now=301;Assert.Throws<LoopbackTimelineException>(()=>r.Put(r.Packet(3896,225146880,5980878323302)));
        Assert.Equal(7680,r.Bytes.Count);
    }
    [Fact]
    public void SlowCandidateRequiresStableSlopeRatherThanMerelySmallAbsoluteOffset()
    {
        var r=RecordedCandidate();
        r.Put(r.Packet(3895,225146400,5980878224302)); // +0.1ms per packet exceeds original one-frame slope tolerance.
        r.Put(r.Packet(3896,225146880,5980878325302));Assert.Equal(4,r.Policy.Pending);Assert.Equal(7680,r.Bytes.Count);
    }
    private static AudioPacketEventArgs Sized(Run r,long seq,long device,long qpc,int frames,byte value=0x44)
    {
        var o=new AudioPacketObservation(seq,48000,8,frames,device,qpc,0,qpc+50000,qpc+50010){InputIdentity=r.Source};o.CompleteRelease(qpc+50020,qpc+50030,true);
        return new(Enumerable.Repeat(value,frames*8).ToArray(),frames*8,frames,device,qpc,qpc,true,observation:o);
    }
    [Theory]
    [InlineData(3840)] [InlineData(3841)]
    public void SlowPendingRejectsFrameBudgetAtOrBeyondExactHundredMilliseconds(int frames)
    {
        var r=RecordedCandidate(); // Already 960 frames held; next reaches/exceeds 4800, and has not converged.
        Assert.Throws<LoopbackTimelineException>(()=>r.Put(Sized(r,3895,225146400,5980878233302,frames)));
        Assert.Equal(7680,r.Bytes.Count);Assert.InRange(r.Policy.Pending,2,3);
    }
    [Fact]
    public void SlowPendingTotalClonedPcmNeverExceedsOneMebibyte()
    {
        // Artificial wide format isolates the byte cap before either frame or packet cap.
        var f=new WaveFormat(48000,32,1024);var t=new LoopbackTimeline(f,10000000,TimeSpan.FromMilliseconds(100));
        t.Start(10000000);var p=new LoopbackIdleResumePolicy(t,f,10000000){Milliseconds=()=>0};var source=new LoopbackIdleEvidence();long written=0;
        AudioPacketEventArgs Packet(int seq,long d,long q,int flags=0,LoopbackIdleTicket? idle=null)
        {
            var o=new AudioPacketObservation(seq,48000,f.BlockAlign,64,d,q,flags,q+50000,q+50010){InputIdentity=source,IdleBefore=idle};o.CompleteRelease(q+50020,q+50030,true);
            return new(new byte[64*f.BlockAlign],64*f.BlockAlign,64,d,q,q,true,observation:o);
        }
        void Put(AudioPacketEventArgs x)=>p.Append(x,1,(_,_,n)=>written+=n,(_,n)=>written+=n);
        Put(Packet(1,0,10000000));Put(Packet(2,64,10013333,2));long before=written,first=15026666;
        var ticket=new LoopbackIdleTicket(source,10063433,first-650000,first+50000,8,700000,2,10063363,64,64,10013333,2);
        Put(Packet(3,128,first,idle:ticket));
        for(int i=1;i<4;i++)Put(Packet(3+i,128+64*i,first+i*13333+i*30000));
        Assert.Equal(4,p.Pending);Assert.Equal(1048576,p.Pending*64*f.BlockAlign);
        Assert.Throws<LoopbackTimelineException>(()=>Put(Packet(7,384,first+4*13333+120000)));
        Assert.Equal(4,p.Pending);Assert.Equal(before,written);Assert.Equal(0,p.Snapshot()[0]);
    }
    [Theory]
    [InlineData("count")] [InlineData("cumulative")] [InlineData("reference")]
    public void SlowIdleSpendsTheSameExistingLifetimePhaseBudgetBeforeAnyPcmCommit(string mode)
    {
        var r=new Run();long seq=3519;
        int steps=mode=="count"?4:3;
        for(int i=0;i<steps;i++)
        {
            long step=mode=="count"?10000:mode=="cumulative"?(i%2==0?310000:-310000):310000;
            r.Device+=480;r.Qpc+=100000+step;r.Put(r.Packet(++seq,r.Device,r.Qpc,2,0));
            r.Device+=480;r.Qpc+=100000;r.Put(r.Packet(++seq,r.Device,r.Qpc,2,0));
        }
        Assert.Equal(steps,r.Timeline.ConfirmedQpcPhaseRebaseCount);
        long before=r.Bytes.Count,phaseBefore=r.Timeline.CumulativeAbsoluteQpcPhaseAdjustmentTicks;
        long q=r.Qpc+5100000;
        r.Put(r.Packet(seq+1,r.Device+480,q,ticket:r.Ticket(500) with{PreviousSequence=seq}));
        r.Put(r.Packet(seq+2,r.Device+960,q+192883));r.Put(r.Packet(seq+3,r.Device+1440,q+292883));
        Assert.Throws<LoopbackTimelineException>(()=>r.Put(r.Packet(seq+4,r.Device+1920,q+392883)));
        Assert.Equal(before,r.Bytes.Count);Assert.Equal(0,r.Policy.Snapshot()[0]);Assert.Equal(4,r.Policy.Pending);
        Assert.Equal(phaseBefore,r.Timeline.CumulativeAbsoluteQpcPhaseAdjustmentTicks);Assert.Equal(steps,r.Timeline.ConfirmedQpcPhaseRebaseCount);
    }
    [Theory]
    [InlineData(500,4)] [InlineData(9000,3)]
    public void RepeatedSlowPausesCannotResetIdleOrPhaseBudgets(int ms,int allowed)
    {
        var r=new Run();long seq=3519;
        for(int i=0;i<allowed;i++)
        {
            long q=r.Qpc+ms*10000L+100000;
            r.Put(r.Packet(seq+1,r.Device+480,q,ticket:r.Ticket(ms) with{PreviousSequence=seq}));
            for(int n=1;n<=3;n++)r.Put(r.Packet(seq+1+n,r.Device+(1+n)*480,q+n*100000+92883,n==3?2:0,n==3?(byte)0:(byte)(0x22+n)));
            seq+=4;r.Device+=1920;r.Qpc=q+392883;
        }
        Assert.Equal(allowed,r.Policy.Snapshot()[0]);Assert.Equal(allowed,r.Timeline.ConfirmedQpcPhaseRebaseCount);
        Assert.Equal(allowed*92883,r.Timeline.CumulativeAbsoluteQpcPhaseAdjustmentTicks);
        long before=r.Bytes.Count;
        Assert.Throws<LoopbackTimelineException>(()=>r.Put(r.Packet(seq+1,r.Device+480,r.Qpc+ms*10000L+100000,ticket:r.Ticket(ms) with{PreviousSequence=seq})));
        Assert.Equal(before,r.Bytes.Count);
    }
    [Theory]
    [InlineData(200000)] [InlineData(200001)] [InlineData(-90000)]
    public void SlowConfirmationExcursionEnvelopeHasAnExactBoundAndSupportsMonotonicNegativeSettling(long offset)
    {
        var r=new Run();long q=r.Qpc+5100000;r.Put(r.Packet(3520,r.Device+480,q,ticket:r.Ticket(500)));
        if(offset>200000)
        {
            Assert.Throws<LoopbackTimelineException>(()=>r.Put(r.Packet(3521,r.Device+960,q+100000+offset)));
            Assert.Equal(7680,r.Bytes.Count);Assert.Equal(1,r.Policy.Pending);return;
        }
        for(int i=1;i<=3;i++)r.Put(r.Packet(3520+i,r.Device+(1+i)*480,q+i*100000+offset));
        Assert.Equal(0,r.Policy.Pending);Assert.Equal(1,r.Policy.Snapshot()[0]);
        Assert.Equal(Math.Abs(offset),r.Timeline.CumulativeAbsoluteQpcPhaseAdjustmentTicks);
        Assert.Equal(7680+192000+4*3840,r.Bytes.Count);
    }
    [Fact]
    public void ShortEmptyWindowAndDeliveredSilentPacketsDoNotBecomeNormalIdle()
    {
        var r=new Run();long q=r.Qpc+100000;
        r.Put(r.Packet(3520,r.Device+480,q,2,0));Assert.Equal(0,r.Policy.Pending);
        r.Put(r.Packet(3521,r.Device+960,q+100000,2,0));Assert.Equal(0,r.Policy.Snapshot()[0]);
        var ticket=new LoopbackIdleTicket(r.Source,q+150100,q+400000,q+650000,2,250000,3521,q+150030,r.Device+960,480,q+100000,2);
        r.Put(r.Packet(3522,r.Device+1440,q+600000,ticket:ticket));
        Assert.Equal(0,r.Policy.Pending);Assert.Equal(0,r.Policy.Snapshot()[0]);
        Assert.Equal(5*3840,r.Bytes.Count); // 50ms no-packet ticket is below the 100ms idle admission floor.
    }
    [Fact]
    public void Recorded3520And3521MustRetainBothSentinelsWithoutEpochReopen()
    {
        var r=new Run();
        // Raw prior/current positions are real. Interior maximum poll gap was not recorded;
        // 70ms is an explicit synthetic assumption that new native code must measure.
        var ticket=new LoopbackIdleTicket(r.Source,5836883237175,5836928524584,5836929147812,74,700000,3519,5836883237087,r.Device,480,r.Qpc,2);
        var first=r.Packet(3520,207955680,5836929097699,ticket:ticket);
        Assert.Equal(0,r.Put(first).TotalBytesWritten);Assert.Equal(7680,r.Bytes.Count);
        var result=r.Put(r.Packet(3521,207956160,5836929203934));
        long shift=45633171;long expectedNormal=(long)(shift/10000000d*384000);expectedNormal-=expectedNormal%8;
        Assert.Equal(expectedNormal,result.NormalIdleZeroBytes);Assert.Equal(expectedNormal+3840,result.ZeroBytesWritten);
        Assert.Equal(7680,result.PacketBytesWritten);Assert.Equal(0,result.PacketBytesSkipped);
        Assert.All(r.Bytes.Take(3840),b=>Assert.Equal(0x11,b));Assert.All(r.Bytes.TakeLast(7680),b=>Assert.Equal(0x22,b));
        Assert.Equal(7680+result.TotalBytesWritten,r.Timeline.MediaBytes);Assert.Equal(0,r.Timeline.QpcOutlierCount);
        Assert.Equal(0,r.Timeline.ConfirmedQpcPhaseRebaseCount);Assert.Equal(1,r.Policy.Snapshot()[0]);
        Assert.Equal(0,r.Policy.Pending);Assert.False(r.Timeline.ContinuityDegraded);
        r.Policy.RequireSettled();
        var diagnostic=new LoopbackPacketDiagnostics();diagnostic.SetIdlePolicy(r.Policy.Snapshot());
        var json=diagnostic.Export(10000000);Assert.True(LoopbackClockDiagnosticReader.TryRead(LoopbackPacketDiagnostics.Prefix+json,out var payload));
        Assert.Equal(1,payload.GetProperty("idle_policy")[0].GetInt64());Assert.Equal(expectedNormal,payload.GetProperty("idle_policy")[1].GetInt64());
        var legacy=JsonNode.Parse(json)!;legacy.AsObject().Remove("idle_policy");legacy["idle_evidence_version"]=2;
        Assert.True(LoopbackClockDiagnosticReader.TryRead(LoopbackPacketDiagnostics.Prefix+legacy.ToJsonString(),out _));
    }
    [Theory]
    [InlineData(100)] [InlineData(500)] [InlineData(1000)] [InlineData(3000)] [InlineData(5000)]
    public void ProvenIdleBridgesOnceAndReturnsToOriginalClockGuards(int ms)
    {
        var r=new Run();var q=r.Qpc+ms*10000L+100000;
        var ticket=r.Ticket(ms,maxPoll:Math.Min(700000,ms*5000));
        r.Put(r.Packet(3520,r.Device+480,q,ticket:ticket));
        var result=r.Put(r.Packet(3521,r.Device+960,q+100000));
        Assert.Equal(ms*384L,result.NormalIdleZeroBytes);Assert.Equal(result.NormalIdleZeroBytes,result.ZeroBytesWritten);
        r.Put(r.Packet(3522,r.Device+1440,q+200000));r.Put(r.Packet(3523,r.Device+1920,q+300000));
        r.Put(r.Packet(3524,r.Device+2400,q+400000));
        Assert.Equal(3840+3840+ms*384L+5*3840,r.Bytes.Count);Assert.False(r.Timeline.ContinuityDegraded);
        Assert.Equal(0,r.Timeline.CumulativeAbsoluteQpcPhaseAdjustmentTicks);
    }
    [Theory]
    [InlineData("unknown_poll")] [InlineData("stall")] [InlineData("consumer_stall")] [InlineData("wrong_qpc")]
    [InlineData("gap")] [InlineData("flag")] [InlineData("timestamp")] [InlineData("source")] [InlineData("generation")]
    public void MissingOrContradictoryEvidenceCannotGrantIdleExemption(string kind)
    {
        var r=new Run();var t=r.Ticket();
        if(kind=="unknown_poll")t=t with{MaxPollGap=0};if(kind=="stall")t=t with{MaxPollGap=20_000_000};
        if(kind=="consumer_stall")t=t with{Start=t.Start+20_000_000};
        var q=r.Qpc+50_100_000;var d=r.Device+480;
        if(kind=="wrong_qpc")q+=10_000_000;if(kind=="gap")d+=48000;
        var p=r.Packet(3520,d,q,kind=="flag"?1:kind=="timestamp"?4:0,ticket:t,source:kind=="source"?new():null);
        Assert.ThrowsAny<Exception>(()=>r.Put(p,kind=="generation"?2:1));
        Assert.Equal(0,r.Policy.Snapshot()[0]);Assert.Equal(7680,r.Bytes.Count);
    }
    [Theory]
    [InlineData("bad_clock")] [InlineData("device_back")] [InlineData("backwards")] [InlineData("source")] [InlineData("deadline")] [InlineData("stop")]
    public void PendingPcmCannotLeakIntoSuccessfulFinalization(string mode)
    {
        var r=new Run();var q=r.Qpc+50_100_000;r.Put(r.Packet(3520,r.Device+480,q,ticket:r.Ticket()));
        if(mode=="stop")Assert.Throws<LoopbackTimelineException>(()=>r.Policy.RequireSettled());
        else
        {
            if(mode=="deadline")r.Now=151;
            var packet=r.Packet(3521,mode=="device_back"?r.Device:r.Device+960,mode=="backwards"?q-1:q+100000+(mode=="bad_clock"?100000:0),source:mode=="source"?new():null);
            if(mode=="bad_clock")
            {
                // Preserve the original 10ms counterexample; it is now held, never committed without successors.
                r.Put(packet);Assert.Equal(2,r.Policy.Pending);r.Now=301;
                Assert.Throws<LoopbackTimelineException>(()=>r.Policy.CheckDeadline());
            }
            else Assert.Throws<LoopbackTimelineException>(()=>r.Put(packet));
        }
        Assert.Equal(7680,r.Bytes.Count);Assert.Equal(0,r.Policy.Snapshot()[0]);
    }
    [Theory]
    [InlineData(500,4)] [InlineData(9000,3)]
    public void RepeatedPausesSpendLifetimeCountAndTimeBudgets(int milliseconds,int allowed)
    {
        var r=new Run();long seq=3519;
        for(int i=0;i<allowed;i++)
        {
            long q=r.Qpc+milliseconds*10000L+100000;
            var ticket=r.Ticket(milliseconds) with{PreviousSequence=seq};
            r.Put(r.Packet(seq+1,r.Device+480,q,ticket:ticket));r.Put(r.Packet(seq+2,r.Device+960,q+100000));
            r.Put(r.Packet(seq+3,r.Device+1440,q+200000,2,0));r.Put(r.Packet(seq+4,r.Device+1920,q+300000,2,0));
            r.Device+=1920;r.Qpc=q+300000;seq+=4;
        }
        long before=r.Timeline.MediaBytes;
        Assert.Throws<LoopbackTimelineException>(()=>r.Put(r.Packet(seq+1,r.Device+480,r.Qpc+milliseconds*10000L+100000,ticket:r.Ticket(milliseconds) with{PreviousSequence=seq})));
        Assert.Equal(allowed,r.Policy.Snapshot()[0]);Assert.Equal(before,r.Timeline.MediaBytes);
    }
    [Fact]
    public void EmptyPollsWithAlmostNoSamplingClockAdvanceAreNotNormalIdle()
    {
        var r=new Run();var ticket=r.Ticket(100,maxPoll:500000) with {ResumeGet=r.Qpc+1050100,LastEmpty=r.Qpc+550100};
        var p=r.Packet(3520,r.Device+480,r.Qpc+110000,ticket:ticket);
        var native=new AudioPacketObservation(3520,48000,8,480,p.DevicePosition,p.QpcPosition,0,ticket.ResumeGet,ticket.ResumeGet+10){InputIdentity=r.Source,IdleBefore=ticket};native.CompleteRelease(ticket.ResumeGet+20,ticket.ResumeGet+30,true);
        p=new(p.Buffer,p.BytesRecorded,p.FramesRecorded,p.DevicePosition,p.QpcPosition,p.PacketStartTimestampTicks,true,observation:native);
        r.Put(p);Assert.Equal(0,r.Policy.Pending);Assert.Equal(0,r.Policy.Snapshot()[0]);Assert.True(r.Timeline.ContinuityDegraded);
    }
    [Theory]
    [InlineData(500,3)] [InlineData(9000,2)]
    public void OfficialEpochHandoffCannotResetRemainingIdleLifetimeBudget(int ms,int additional)
    {
        var r=new Run();long q=r.Qpc+ms*10000L+100000;
        r.Put(r.Packet(3520,r.Device+480,q,ticket:r.Ticket(ms)));r.Put(r.Packet(3521,r.Device+960,q+100000));
        var before=r.Policy.Snapshot();var identity=new LoopbackIdleEvidence();
        r.Policy.CommitEpoch(2,identity);Assert.Equal(before,r.Policy.Snapshot());
        long device=0,previous=20_000_000,seq=1;r.Timeline.RebaseAfterEpochReset(device,previous);
        r.Put(r.Packet(seq,device,previous,2,0,source:identity),2);Assert.True(r.Timeline.ContinuityDegraded);
        for(int n=0;n<additional;n++)
        {
            long first=previous+ms*10000L+100000;
            var ticket=new LoopbackIdleTicket(identity,previous+50100,first+50000-700000,first+50000,Math.Max(2,ms/60),700000,seq,previous+50030,device,480,previous,2);
            r.Put(r.Packet(seq+1,device+480,first,ticket:ticket,source:identity),2);r.Put(r.Packet(seq+2,device+960,first+100000,source:identity),2);
            r.Put(r.Packet(seq+3,device+1440,first+200000,2,0,source:identity),2);r.Put(r.Packet(seq+4,device+1920,first+300000,2,0,source:identity),2);
            seq+=4;device+=1920;previous=first+300000;
        }
        long count=r.Policy.Snapshot()[0],bytes=r.Timeline.MediaBytes;
        var finalTicket=new LoopbackIdleTicket(identity,previous+50100,previous+ms*10000L-550000,previous+ms*10000L+150000,Math.Max(2,ms/60),700000,seq,previous+50030,device,480,previous,2);
        Assert.Throws<LoopbackTimelineException>(()=>r.Put(r.Packet(seq+1,device+480,previous+ms*10000L+100000,ticket:finalTicket,source:identity),2));
        Assert.Equal(1+additional,count);Assert.Equal(bytes,r.Timeline.MediaBytes);Assert.True(r.Policy.Snapshot()[3]>0);
    }
    [Theory]
    [InlineData("no_handoff_source")] [InlineData("no_handoff_generation")]
    [InlineData("wrong_winner_source")] [InlineData("wrong_winner_generation")] [InlineData("invalid_first")]
    public void DifferentInputDoesNotImplicitlyAuthorizeHandoffOrBypassFirstPacketChecks(string mode)
    {
        var r=new Run();long q=r.Qpc+50_100_000;r.Put(r.Packet(3520,r.Device+480,q,ticket:r.Ticket()));r.Put(r.Packet(3521,r.Device+960,q+100000));
        var identity=new LoopbackIdleEvidence();bool formal=!mode.StartsWith("no_");
        if(formal){r.Policy.CommitEpoch(2,identity);r.Timeline.RebaseAfterEpochReset(0,20000000);}
        long bytes=r.Timeline.MediaBytes;
        var packet=r.Packet(formal?1:3522,formal?0:r.Device+1440,formal?20000000:q+200000,mode=="invalid_first"?4:0,
            source:mode is "wrong_winner_source" or "no_handoff_source"?new():formal?identity:r.Source);
        Assert.Throws<LoopbackTimelineException>(()=>r.Put(packet,mode is "wrong_winner_generation" or "no_handoff_generation"?3:formal?2:1));
        Assert.Equal(bytes,r.Timeline.MediaBytes);Assert.Equal(1,r.Policy.Snapshot()[0]);
    }
}
