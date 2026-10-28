using System.Diagnostics;
using NAudio.Wave;

namespace AgentRecorder.AudioHelper;

/// <summary>
/// Optional position-aware packet seam. Only the loopback path consumes this
/// event; microphone and HFP retain the legacy DataAvailable contract.
/// </summary>
internal interface IAudioPacketPositionSource
{
    event EventHandler<AudioPacketEventArgs>? PacketPositionAvailable;
}

internal sealed class AudioPacketEventArgs : EventArgs
{
    public AudioPacketEventArgs(
        byte[] buffer,
        int bytesRecorded,
        int framesRecorded,
        long devicePosition,
        long qpcPosition,
        long packetStartTimestampTicks,
        bool positionValid,
        bool dataDiscontinuity = false,
        AudioPacketObservation? observation = null,
        int chunkOffsetFrames = 0)
    {
        Buffer = buffer ?? throw new ArgumentNullException(nameof(buffer));
        if (bytesRecorded < 0 || bytesRecorded > buffer.Length)
            throw new ArgumentOutOfRangeException(nameof(bytesRecorded));
        if (framesRecorded < 0)
            throw new ArgumentOutOfRangeException(nameof(framesRecorded));

        BytesRecorded = bytesRecorded;
        FramesRecorded = framesRecorded;
        DevicePosition = devicePosition;
        QpcPosition = qpcPosition;
        PacketStartTimestampTicks = packetStartTimestampTicks;
        PositionValid = positionValid;
        DataDiscontinuity = dataDiscontinuity;
        Observation = observation;
        ChunkOffsetFrames = chunkOffsetFrames;
    }

    public byte[] Buffer { get; }
    public int BytesRecorded { get; }
    public int FramesRecorded { get; }
    public long DevicePosition { get; }
    public long QpcPosition { get; }
    public long PacketStartTimestampTicks { get; }
    public bool PositionValid { get; }
    public bool DataDiscontinuity { get; }
    internal AudioPacketObservation? Observation { get; }
    internal int ChunkOffsetFrames { get; }
    internal long DiagnosticSequence { get; set; }
    internal long DiagnosticGeneration { get; set; }
    internal long DiagnosticReceivedTicks { get; set; }

    public AudioPacketEventArgs Clone()
        => new(Buffer[..BytesRecorded], BytesRecorded, FramesRecorded, DevicePosition,
            QpcPosition, PacketStartTimestampTicks, PositionValid, DataDiscontinuity,
            Observation, ChunkOffsetFrames) { DiagnosticSequence = DiagnosticSequence,
                DiagnosticGeneration = DiagnosticGeneration, DiagnosticReceivedTicks = DiagnosticReceivedTicks };
}

// Metadata only. Release completion is published atomically after callbacks
// return; retaining this object never retains a native pointer or audio bytes.
internal sealed class AudioPacketObservation(
    long inputSequence, int sampleRate, int blockAlign, int nativeFrames,
    long rawDevicePosition, long rawQpcPosition, int flags,
    long getBufferStartTicks, long getBufferEndTicks,
    int? bufferCapacityFrames = null, int? pollSleepMilliseconds = null)
{
    internal long InputSequence { get; } = inputSequence;
    internal int SampleRate { get; } = sampleRate;
    internal int BlockAlign { get; } = blockAlign;
    internal int NativeFrames { get; } = nativeFrames;
    internal long RawDevicePosition { get; } = rawDevicePosition;
    internal long RawQpcPosition { get; } = rawQpcPosition;
    internal int Flags { get; } = flags;
    internal long GetBufferStartTicks { get; } = getBufferStartTicks;
    internal long GetBufferEndTicks { get; } = getBufferEndTicks;
    internal int? BufferCapacityFrames { get; } = bufferCapacityFrames;
    internal int? PollSleepMilliseconds { get; } = pollSleepMilliseconds;
    private PacketReleaseObservation? _release;
    internal PacketReleaseObservation? Release => Volatile.Read(ref _release);
    internal void CompleteRelease(long start, long end, bool success) =>
        Volatile.Write(ref _release, new(start, end, success));
}

internal sealed record PacketReleaseObservation(long StartTicks, long EndTicks, bool Success);

internal static class AudioPacketPositionMath
{
    // IAudioCaptureClient reports QPCPosition in 100-nanosecond units.
    internal const long QpcUnitsPerSecond = 10_000_000L;

    public static long ConvertQpcToStopwatchTicks(long qpcPosition, long callbackTimestamp, long stopwatchFrequency)
    {
        if (qpcPosition <= 0 || callbackTimestamp <= 0 || stopwatchFrequency <= 0)
            throw new ArgumentOutOfRangeException(nameof(qpcPosition));

        // Both values originate from QueryPerformanceCounter. Sampling the
        // current Stopwatch timestamp at callback time lets us remove the
        // callback delay while retaining the WASAPI packet's recorded time.
        double callbackQpc100ns = callbackTimestamp * (double)QpcUnitsPerSecond / stopwatchFrequency;
        double packetDelta100ns = callbackQpc100ns - qpcPosition;
        double packetTicks = callbackTimestamp - packetDelta100ns * stopwatchFrequency / QpcUnitsPerSecond;
        if (double.IsNaN(packetTicks) || double.IsInfinity(packetTicks) ||
            packetTicks <= 0 || packetTicks > long.MaxValue)
            throw new InvalidOperationException("WASAPI packet QPC could not be converted to Stopwatch ticks");

        return (long)packetTicks;
    }

    public static long FramesToTimestampTicks(long frames, int sampleRate, long timestampFrequency)
    {
        if (frames < 0 || sampleRate <= 0 || timestampFrequency <= 0)
            throw new ArgumentOutOfRangeException(nameof(frames));

        double ticks = frames * (double)timestampFrequency / sampleRate;
        if (ticks > long.MaxValue)
            throw new OverflowException("Audio frame duration exceeds timestamp range");
        return (long)ticks;
    }
}
