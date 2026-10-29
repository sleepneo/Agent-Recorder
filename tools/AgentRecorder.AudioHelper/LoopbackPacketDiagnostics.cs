using System.Diagnostics;
using System.Text.Json;
using NAudio.Wave;

namespace AgentRecorder.AudioHelper;

// A metadata-only flight recorder. It never owns packet buffers or drives the
// media clock. One terminal stderr export is bounded independently of duration.
internal sealed class LoopbackPacketDiagnostics
{
    internal const int Capacity = 64;
    internal const int StateCapacity = 8;
    internal const int MaxJsonChars = 24000;
    internal const string Prefix = "LoopbackClockDiagnosticsV1: ";
    internal static readonly string[] Columns =
    {
        "sequence", "generation", "input_sequence", "sample_rate", "block_align",
        "native_frames", "raw_device", "raw_qpc_100ns", "raw_flags",
        "get_buffer_start_ticks", "get_buffer_end_ticks", "release_start_ticks", "release_end_ticks", "release_ok",
        "chunk_offset_frames", "frames", "device", "converted_packet_ticks", "received_ticks",
        "position_valid", "data_discontinuity", "outcome", "written_bytes", "skipped_bytes", "zero_bytes",
        "fault_kind", "timeline_before", "timeline_after", "buffer_capacity_frames", "poll_sleep_ms"
    };
    internal static readonly string[] TimelineColumns =
    {
        "anchor_ticks", "anchor_device", "anchor_media_bytes", "media_bytes", "last_written_device_start",
        "last_written_device_end", "trusted_qpc_ticks", "trusted_device", "pending_qpc_ticks", "pending_device",
        "outliers", "consecutive_outliers", "confirmed_phase_rebases", "absolute_phase_adjustment_ticks",
        "phase_budget_ticks", "jitter_tolerance_ticks", "max_device_gap_frames"
    };
    private readonly object _gate = new();
    private readonly Entry?[] _entries = new Entry[Capacity];
    private readonly List<long[]> _states = new(StateCapacity);
    private long _sequence;
    private long? _faultSequence;
    private Entry? _faultEntry;
    private long _statesOmitted;
    private long[] _idlePolicy=new long[13];
    internal void SetIdlePolicy(long[] summary)
    { lock(_gate) { if(summary.Length!=13)throw new ArgumentException("Invalid idle summary");_idlePolicy=(long[])summary.Clone(); } }
    private readonly List<(long Generation, LoopbackIdleEvidence Evidence)> _idleSources = new(4);
    private long _idleSourcesOmitted;
    private long _faultIdleGeneration = -1;
    private long[]? _faultIdleBinding;
    private void TrimIdleSources()
    {
        while (_idleSources.Count > 4)
        {
            int remove = _idleSources.FindIndex(1,s => s.Generation != _faultIdleGeneration);
            _idleSources.RemoveAt(remove); _idleSourcesOmitted++;
        }
    }
    internal void RegisterIdleEvidence(long generation, LoopbackIdleEvidence evidence)
    {
        lock (_gate)
        {
            _idleSources.Add((generation, evidence));
            TrimIdleSources();
        }
    }

    internal long Observe(AudioPacketEventArgs packet, long generation, WaveFormat? format, long receivedTicks, long[]? before)
    {
        lock (_gate)
        {
            long sequence = ++_sequence;
            _entries[(sequence - 1) % Capacity] = MakeEntry(sequence, generation, packet, format, receivedTicks, before);
            return sequence;
        }
    }

    internal void BeforeAppend(AudioPacketEventArgs packet, WaveFormat? format, long[] before)
    {
        if (packet.DiagnosticSequence <= 0) return;
        lock (_gate)
        {
            long sequence = packet.DiagnosticSequence;
            var entry = _entries[(sequence - 1) % Capacity];
            if (entry?.Sequence != sequence)
                _entries[(sequence - 1) % Capacity] = entry = MakeEntry(sequence,
                    packet.DiagnosticGeneration, packet, format, packet.DiagnosticReceivedTicks, before);
            if (!ReferenceEquals(entry, _faultEntry)) entry.Before = before;
        }
    }

    // outcome: 0 received, 1 pre-start buffered, 2 recovery buffered, 3 appended,
    // 4 rejected, 5 discarded candidate, 6 accepted isolated QPC outlier.
    internal void Finish(long sequence, int outcome, long? written, long? skipped, long? zeros, long[]? after, int faultKind = 0)
    {
        if (sequence <= 0) return;
        lock (_gate)
        {
            var entry = _entries[(sequence - 1) % Capacity];
            if (entry?.Sequence != sequence) return;
            if (ReferenceEquals(entry, _faultEntry)) return; // A revisit cannot rewrite the first rejection.
            entry.Outcome = outcome; entry.Written = written; entry.Skipped = skipped;
            entry.Zeros = zeros; entry.After = after; entry.FaultKind = faultKind;
            if (outcome == 4 && _faultEntry is null)
            {
                _faultSequence = sequence; _faultEntry = entry;
                _faultIdleGeneration = entry.Generation;
                if (entry.IdleSource is not null)
                {
                    if (entry.IdleContext is not null) entry.IdleSource.Pin(entry.IdleContext);
                    if (!_idleSources.Any(s => s.Generation == entry.Generation))
                    { _idleSources.Add((entry.Generation,entry.IdleSource)); _idleSourcesOmitted--; }
                    _idleSources.Sort((a,b) => a.Generation.CompareTo(b.Generation));
                    TrimIdleSources();
                }
                // status: 1 exact membership, 0 source present/no match, 2 source absent, 3 native sequence absent.
                _faultIdleBinding = new[] { entry.Generation, entry.NativeSequence, entry.IdleContext?.Start ?? -1,
                    entry.NativeSequence <= 0 ? 3L : entry.IdleSource is null ? 2L : entry.IdleContext is null ? 0L : 1L };
            }
        }
    }

    // Fixed codes only, no exception/device/path text. Keep the first fault
    // window stable while bounded recovery/retirement state remains observable.
    internal void State(int code, long generation, long value = 0)
    {
        lock (_gate)
        {
            if (_states.Count == StateCapacity) { _statesOmitted++; _states.RemoveAt(0); }
            _states.Add(new[] { Stopwatch.GetTimestamp(), generation, (long)code, value });
        }
    }

    internal string Export(long timestampFrequency)
    {
        lock (_gate)
        {
            var retained = _entries.Where(x => x is not null).Select(x => x!).OrderBy(x => x.Sequence).ToList();
            if (_faultEntry is not null && !retained.Contains(_faultEntry))
            { retained.RemoveAt(0); retained.Add(_faultEntry); retained.Sort((a, b) => a.Sequence.CompareTo(b.Sequence)); }
            var rows = retained.Select(x => x.Row()).ToList();
            string result;
            do
            {
                result = JsonSerializer.Serialize(new
                {
                    version = 1, timestamp_frequency = timestampFrequency, qpc_units_per_second = AudioPacketPositionMath.QpcUnitsPerSecond,
                    helper_mvid = typeof(CaptureSession).Assembly.ManifestModule.ModuleVersionId,
                    naudio_core_mvid = typeof(WaveFormat).Assembly.ManifestModule.ModuleVersionId,
                    naudio_wasapi_mvid = typeof(NAudio.CoreAudioApi.AudioClient).Assembly.ManifestModule.ModuleVersionId,
                    capacity = Capacity, total_observed = _sequence, omitted_packets = _sequence - rows.Count,
                    fault_sequence = _faultSequence, fault_record_pinned = _faultEntry is not null,
                    columns = Columns, timeline_columns = TimelineColumns, packets = rows,
                    states = _states, states_omitted = _statesOmitted,
                    idle_evidence_version = 3,
                    idle_policy = _idlePolicy,
                    idle_fault_binding = _faultIdleBinding,
                    idle_sources_omitted = _idleSourcesOmitted,
                    idle_sources = _idleSources.Select(s => new { generation = s.Generation, evidence = s.Evidence.Snapshot() }).ToArray()
                });
                if (result.Length <= MaxJsonChars) return result;
                // Preserve the pinned fault and newest evidence under the byte budget.
                int remove = rows.Count > 1 && (long)rows[0][0]! == _faultSequence ? 1 : 0;
                rows.RemoveAt(remove);
            } while (rows.Count > 0);
            throw new InvalidOperationException("Clock diagnostic metadata exceeded its fixed budget");
        }
    }

    private sealed class Entry
    {
        internal readonly long Sequence, Generation;
        internal long NativeSequence => _native?.InputSequence ?? -1;
        internal LoopbackIdleEvidence? IdleSource;
        internal LoopbackIdleEvidence.Context? IdleContext;
        private readonly AudioPacketObservation? _native;
        private readonly int? _sampleRate, _blockAlign;
        private readonly int _offset, _frames;
        private readonly long _device, _converted, _received;
        private readonly bool _valid, _discontinuity;
        internal long[]? Before;
        internal int Outcome, FaultKind;
        internal long? Written, Skipped, Zeros;
        internal long[]? After;
        internal Entry(long sequence, long generation, AudioPacketEventArgs packet, WaveFormat? format, long received, long[]? before)
        {
            Sequence = sequence; Generation = generation; _native = packet.Observation;
            _sampleRate = _native?.SampleRate ?? format?.SampleRate; _blockAlign = _native?.BlockAlign ?? format?.BlockAlign;
            _offset = packet.ChunkOffsetFrames; _frames = packet.FramesRecorded;
            _device = packet.DevicePosition; _converted = packet.PacketStartTimestampTicks; _received = received;
            _valid = packet.PositionValid; _discontinuity = packet.DataDiscontinuity; Before = before;
        }
        internal object?[] Row()
        {
            var release = _native?.Release;
            return new object?[]
            {
                Sequence, Generation, _native?.InputSequence, _sampleRate, _blockAlign,
                _native?.NativeFrames, _native?.RawDevicePosition, _native?.RawQpcPosition, _native?.Flags,
                _native?.GetBufferStartTicks, _native?.GetBufferEndTicks, release?.StartTicks, release?.EndTicks, release?.Success,
                _offset, _frames, _device, _converted, _received, _valid, _discontinuity, Outcome,
                Written, Skipped, Zeros, FaultKind, Before, After, _native?.BufferCapacityFrames, _native?.PollSleepMilliseconds
            };
        }
    }
    private Entry MakeEntry(long sequence, long generation, AudioPacketEventArgs packet, WaveFormat? format, long receivedTicks, long[]? before)
    {
        var e = new Entry(sequence,generation,packet,format,receivedTicks,before);
        e.IdleSource = _idleSources.FirstOrDefault(s => s.Generation == generation).Evidence;
        if (e.NativeSequence > 0) e.IdleContext = e.IdleSource?.Capture(e.NativeSequence);
        return e;
    }
}
