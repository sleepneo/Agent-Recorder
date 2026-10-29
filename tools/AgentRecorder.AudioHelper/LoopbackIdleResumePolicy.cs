using NAudio.Wave;
namespace AgentRecorder.AudioHelper;

// Serialized by the existing session callback/writer owner; never reads audit JSON or endpoint defaults.
internal sealed class LoopbackIdleResumePolicy(LoopbackTimeline timeline, WaveFormat format, long frequency)
{
    // Reuse Timeline's 8-packet/100ms observation envelope and phase classifier.
    // A slow candidate gets 300ms from its FIRST packet (four measured ~63ms poll intervals
    // plus margin); the initial/fast 150ms deadline and ordinary observation stay unchanged.
    // This owner retains native identity/ticket and never nests another PCM queue.
    private readonly List<AudioPacketEventArgs> _pending=new(8);
    private LoopbackIdleEvidence? _identity;
    private long _generation,_lastNative,_deadline,_shift,_totalShift,_count,_normalBytes,_rejected,_span,_maxPoll,_gap,_jitter;
    private int _decision,_warmup;
    private AudioPacketEventArgs? _first,_last;
    private long _expectedEpochGeneration;
    private LoopbackIdleEvidence? _expectedEpochIdentity;
    // Called only by Session after the successful candidate wins, under its existing callback/state locks.
    internal void CommitEpoch(long generation,LoopbackIdleEvidence? identity)
    {
        RequireSettled(); // Pending PCM cannot be discarded as if it were confirmed warmup.
        if(generation<=_generation)throw new LoopbackTimelineException("Obsolete epoch handoff");
        _warmup=0;_first=_last=null;_identity=null;_lastNative=-1;_deadline=0;
        _generation=generation;_expectedEpochGeneration=generation;_expectedEpochIdentity=identity;
        // Historical counters, cumulative budgets, evidence and rejection facts intentionally survive.
    }
    internal Func<long> Milliseconds { get; set; }=()=>Environment.TickCount64;
    internal int Pending => _pending.Count;
    private LoopbackTimelineException Reject(string message)
    {
        if(_decision!=3)_rejected++;_decision=3;
        // The fault packet may have a newer short empty window. Keep the *candidate's*
        // origin identifiable without expanding the bounded diagnostic JSON schema.
        if(Pending!=0 && _first?.Observation is { IdleBefore: { } idle } first)
            message+=$"; idle_candidate_generation={_generation}; idle_candidate_first_native={first.InputSequence}; idle_candidate_window_start={idle.Start}; idle_candidate_pending={Pending}";
        return new(message);
    }
    internal long[] Snapshot()=>new[]{_count,_normalBytes,_totalShift,_rejected,_pending.Count,_span,_maxPoll,_shift,_gap,_jitter,_decision,4L,frequency*30};
    internal void RequireSettled()
    { if(Pending!=0)throw Reject("Unconfirmed same-input idle PCM cannot be finalized"); }
    internal void CheckDeadline()
    {if(Pending!=0 && Milliseconds()>_deadline)throw Reject("Same-input idle confirmation deadline exceeded");}
    private bool CompleteNative(AudioPacketEventArgs p)=>p.Observation is { } o && p.ChunkOffsetFrames==0 &&
        o.SampleRate==format.SampleRate && o.BlockAlign==format.BlockAlign && p.DevicePosition==o.RawDevicePosition && p.QpcPosition==o.RawQpcPosition &&
        p.FramesRecorded==o.NativeFrames && p.BytesRecorded==(long)p.FramesRecorded*format.BlockAlign &&
        p.FramesRecorded>0 && p.FramesRecorded<=format.SampleRate/10 &&
        p.BytesRecorded>0 && p.BytesRecorded<=p.Buffer.Length && p.BytesRecorded<=1024*1024 && p.PositionValid && !p.DataDiscontinuity &&
        (o.Flags & ~2)==0 && o.Release?.Success==true && o.GetBufferEndTicks>=o.GetBufferStartTicks &&
        o.Release.EndTicks-o.GetBufferStartTicks<=frequency/10 &&
        (p.DiagnosticReceivedTicks<=0 || p.DiagnosticReceivedTicks-o.Release.EndTicks<=frequency/10);
    private bool CanBegin(AudioPacketEventArgs p,long generation)
    {
        if(!CompleteNative(p) || p.Observation!.IdleBefore is not { } e || _identity is null ||
            !ReferenceEquals(_identity,p.Observation.InputIdentity) || !ReferenceEquals(_identity,e.Source) || _generation!=generation ||
            e.PreviousSequence!=_lastNative || e.PreviousFrames<=0 || e.PreviousFlags!=2) return false;
        long previousQpc=AudioPacketPositionMath.ConvertQpcToStopwatchTicks(e.PreviousQpc,e.PreviousRelease,frequency);
        if(!timeline.CanMapIdle(e.PreviousDevice,previousQpc))return false;
        long gap=p.DevicePosition-(e.PreviousDevice+e.PreviousFrames);
        if(gap<0 || gap>format.SampleRate/50)return false; // At most 20ms device gap; not a multi-second loss exemption.
        long span=e.ResumeGet-e.Start;
        long shift=timeline.IdleShift(p.DevicePosition,p.PacketStartTimestampTicks);
        if(span<frequency/10 || span>frequency*10 || e.LastEmpty-e.Start<span/2 || e.Polls<2 ||
            e.MaxPollGap<=0 || e.MaxPollGap>frequency*15/100 || e.Start-e.PreviousRelease<0 ||
            e.Start-e.PreviousRelease>frequency/10 || Math.Abs(p.Observation.GetBufferStartTicks-e.ResumeGet)>frequency/100 ||
            shift<=0 || Math.Abs(shift-span)>Math.Min(frequency/10,span/4)) return false;
        if(_count>=4 || shift>frequency*30-_totalShift)throw Reject("Normal idle lifetime budget exhausted");
        _span=span;_maxPoll=e.MaxPollGap;_shift=shift;_gap=gap;_decision=1;return true;
    }
    private bool Follows(AudioPacketEventArgs p,long generation)
    {
        if(!CompleteNative(p) || _last is null || _first is null || generation!=_generation ||
            !ReferenceEquals(_identity,p.Observation!.InputIdentity) || p.Observation.InputSequence!=_last.Observation!.InputSequence+1 ||
            p.DevicePosition!=_last.DevicePosition+_last.FramesRecorded || p.PacketStartTimestampTicks<=_last.PacketStartTimestampTicks ||
            p.DevicePosition+p.FramesRecorded-_first.DevicePosition>format.SampleRate/10)return false;
        long expected=AudioPacketPositionMath.FramesToTimestampTicks(p.DevicePosition-_first.DevicePosition,format.SampleRate,frequency);
        _jitter=p.PacketStartTimestampTicks-_first.PacketStartTimestampTicks-expected;
        return true; // Clock convergence is decided separately, after structural continuity.
    }
    private bool StablePending() => _pending.Count>=3 && timeline.StableObservationTrajectory(
        _pending[^3].DevicePosition,_pending[^3].PacketStartTimestampTicks,
        _pending[^2].DevicePosition,_pending[^2].PacketStartTimestampTicks,
        _pending[^1].DevicePosition,_pending[^1].PacketStartTimestampTicks);
    internal LoopbackPacketAppendResult Append(AudioPacketEventArgs p,long generation,Action<byte[],int,int> write,Action<byte[],int> zeros)
    {
        if(_expectedEpochGeneration!=0 && (generation!=_expectedEpochGeneration ||
            (_expectedEpochIdentity is not null && !ReferenceEquals(_expectedEpochIdentity,p.Observation?.InputIdentity))))
            throw Reject("Packet does not belong to committed epoch");
        CheckDeadline();
        if(Pending==0 && _warmup>0)
        {
            if(!Follows(p,generation) || Math.Abs(_jitter)>frequency/500)throw Reject("Unstable post-idle timestamp trajectory");
            var result=timeline.AppendConfirmedIdlePacket(p,write,zeros);timeline.TrustIdlePacket(p.DevicePosition,p.PacketStartTimestampTicks);
            _last=p.Clone();_lastNative=p.Observation!.InputSequence;_warmup--;return result;
        }
        if(Pending==0 && CanBegin(p,generation))
        { _pending.Add(p.Clone());_first=_last=_pending[0];_deadline=Milliseconds()+150;return new(0,0,0); }
        if(Pending!=0)
        {
            // 20ms (two recorded 10ms packets) bounds the entire excursion from the FIRST
            // packet. It never grants confirmation: slow admission needs two one-frame-accurate
            // intervals and the original lifetime phase budget, not a larger per-packet tolerance.
            if(!Follows(p,generation) || _pending.Count>=8 ||
                _pending.Sum(x=>x.FramesRecorded)+p.FramesRecorded>format.SampleRate/10 ||
                _pending.Sum(x=>x.BytesRecorded)+(long)p.BytesRecorded>1024*1024 || Math.Abs(_jitter)>frequency/50)
                throw Reject("Invalid same-input idle confirmation");
            bool fast=_pending.Count==1 && Math.Abs(_jitter)<=frequency/500;
            if(_pending.Count==1 && !fast)_deadline+=150; // One extension, never a per-packet clock reset.
            _pending.Add(p.Clone());
            _last=_pending[^1];
            if(!fast && !StablePending())
            {
                if(_pending.Count==8 || _pending.Sum(x=>x.FramesRecorded)==format.SampleRate/10)
                    throw Reject("Same-input idle trajectory did not converge within observation budget");
                return new(0,0,0);
            }
            long outliers=_pending.Count(x=>Math.Abs(x.PacketStartTimestampTicks-_first!.PacketStartTimestampTicks-
                AudioPacketPositionMath.FramesToTimestampTicks(x.DevicePosition-_first.DevicePosition,format.SampleRate,frequency))>frequency/500);
            try { timeline.ConfirmIdleTrajectory(_first!,p,_shift,!fast,outliers); }
            catch(LoopbackTimelineException e) { throw Reject(e.Message); }
            long z=0,w=0,s=0;
            foreach(var held in _pending){var r=timeline.AppendConfirmedIdlePacket(held,write,zeros);z+=r.ZeroBytesWritten;w+=r.PacketBytesWritten;s+=r.PacketBytesSkipped;}
            timeline.TrustIdlePacket(p.DevicePosition,p.PacketStartTimestampTicks);
            long normal=Math.Max(0,z-_gap*format.BlockAlign);
            _warmup=Math.Max(0,4-_pending.Count);
            _normalBytes+=normal;_totalShift+=_shift;_count++;_decision=2;_pending.Clear();_last=p.Clone();_lastNative=p.Observation!.InputSequence;
            return new(z,w,s,!fast,normal);
        }
        var ordinary=timeline.AppendPacket(p.Buffer,p.BytesRecorded,p.FramesRecorded,p.DevicePosition,p.PacketStartTimestampTicks,p.PositionValid,write,zeros,p.DataDiscontinuity);
        _expectedEpochGeneration=0;_expectedEpochIdentity=null;
        _identity=p.Observation?.InputIdentity;_generation=generation;_lastNative=p.Observation?.InputSequence??-1;
        return ordinary;
    }
}
