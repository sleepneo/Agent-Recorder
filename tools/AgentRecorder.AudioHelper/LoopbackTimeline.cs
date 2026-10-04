using NAudio.Wave;

namespace AgentRecorder.AudioHelper;

internal readonly record struct LoopbackPacketAppendResult(
    long ZeroBytesWritten,
    long PacketBytesWritten,
    long PacketBytesSkipped,
    bool QpcOutlierAccepted = false)
{
    public long TotalBytesWritten => ZeroBytesWritten + PacketBytesWritten;
}

internal sealed class LoopbackTimelineException : InvalidOperationException
{
    public LoopbackTimelineException(string message) : base(message) { }

    private LoopbackTimelineException(
        string message,
        bool isQpcDevicePositionConflict,
        long qpcDriftTicks,
        long currentDeviceGapFrames,
        bool deviceGapOutOfBounds,
        bool dataDiscontinuity,
        int consecutiveQpcOutliers,
        bool isEpochResetCandidate)
        : base(message)
    {
        IsQpcDevicePositionConflict = isQpcDevicePositionConflict;
        QpcDriftTicks = qpcDriftTicks;
        CurrentDeviceGapFrames = currentDeviceGapFrames;
        DeviceGapOutOfBounds = deviceGapOutOfBounds;
        DataDiscontinuity = dataDiscontinuity;
        ConsecutiveQpcOutliers = consecutiveQpcOutliers;
        IsEpochResetCandidate = isEpochResetCandidate;
    }

    internal bool IsQpcDevicePositionConflict { get; }
    internal long QpcDriftTicks { get; }
    internal long CurrentDeviceGapFrames { get; }
    internal bool DeviceGapOutOfBounds { get; }
    internal bool DataDiscontinuity { get; }
    internal int ConsecutiveQpcOutliers { get; }
    internal bool IsEpochResetCandidate { get; }

    internal static LoopbackTimelineException QpcDevicePositionConflict(
        string message,
        long qpcDriftTicks,
        long currentDeviceGapFrames,
        bool deviceGapOutOfBounds,
        bool dataDiscontinuity,
        int consecutiveQpcOutliers,
        bool isEpochResetCandidate)
        => new(message, true, qpcDriftTicks, currentDeviceGapFrames,
            deviceGapOutOfBounds, dataDiscontinuity, consecutiveQpcOutliers,
            isEpochResetCandidate);
}

/// <summary>
/// Source-aware media clock for a WASAPI loopback stream.
///
/// Packet position and QPC-derived timestamp are the only inputs that advance
/// the media cursor during capture. Wall time is used only by terminal
/// finalization, so a delayed real packet cannot be replaced by timer-written
/// silence before it arrives.
/// </summary>
internal sealed class LoopbackTimeline
{
    private const int MaxZeroChunkBytes = 65536;
    // A single startup phase correction is expected; a few more tolerate
    // transient clock settling, while repeated rebases indicate an unstable
    // timestamp source. The separate 100-ms absolute budget prevents small or
    // alternating steps from evading the count bound's purpose.
    private const int MaxConfirmedQpcPhaseRebasesPerSession = 4;

    private readonly WaveFormat _format;
    private readonly long _timestampFrequency;
    private readonly long _frameTimestampTicks;
    private readonly long _qpcJitterToleranceTicks;
    private readonly long _maxDeviceGapFrames;
    private readonly long _maxQpcPhaseShiftTicks;
    private readonly long _epochResetQpcDriftThresholdTicks;
    private long _anchorTimestamp;
    private long _anchorDevicePosition = -1;
    private long _anchorDeviceMediaBytes;
    private long _mediaBytes;
    private long _lastDeviceStart = -1;
    private long _lastDeviceEnd = -1;
    private long _lastPacketStartTimestamp = -1;
    private long _lastPacketEndTimestamp = -1;
    private long _lastTrustedQpcTimestamp = -1;
    private long _lastTrustedDeviceStart = -1;
    private long _phaseBudgetReferenceQpcTimestamp = -1;
    private long _phaseBudgetReferenceDeviceStart = -1;
    private long _pendingQpcPhaseTimestamp = -1;
    private long _pendingQpcPhaseDeviceStart = -1;
    private long _confirmedQpcPhaseRebaseCount;
    private long _cumulativeAbsoluteQpcPhaseAdjustmentTicks;
    private long _qpcOutlierCount;
    private int _consecutiveQpcOutliers;
    private int _continuityDegraded;

    public LoopbackTimeline(WaveFormat format, long timestampFrequency, TimeSpan paddingTolerance)
    {
        _format = format ?? throw new ArgumentNullException(nameof(format));
        if (_format.AverageBytesPerSecond <= 0 || _format.BlockAlign <= 0 || _format.SampleRate <= 0)
            throw new ArgumentException("Loopback timeline requires a valid byte-aligned audio format", nameof(format));
        if (timestampFrequency <= 0)
            throw new ArgumentOutOfRangeException(nameof(timestampFrequency));
        if (paddingTolerance < TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(paddingTolerance));

        _timestampFrequency = timestampFrequency;
        _epochResetQpcDriftThresholdTicks = timestampFrequency;
        _frameTimestampTicks = AudioPacketPositionMath.FramesToTimestampTicks(
            1, _format.SampleRate, timestampFrequency);
        var qpcQuantizationTicks = Math.Max(1L,
            (long)Math.Ceiling(timestampFrequency / (double)AudioPacketPositionMath.QpcUnitsPerSecond));
        // One audio frame is the maximum unexplained boundary jitter accepted
        // here. It covers frame-to-tick truncation and QPC 100-ns quantization,
        // while remaining tied to the negotiated format rather than a large
        // arbitrary wall-clock tolerance.
        _qpcJitterToleranceTicks = Math.Max(_frameTimestampTicks, qpcQuantizationTicks);
        var maxDeviceGap = _format.SampleRate * paddingTolerance.TotalSeconds;
        if (maxDeviceGap > long.MaxValue)
            throw new ArgumentOutOfRangeException(nameof(paddingTolerance));
        _maxDeviceGapFrames = Math.Max(0L, (long)Math.Floor(maxDeviceGap));
        // A small QPC phase change is considered only when it is bounded by
        // the configured packet-position continuity envelope, then confirmed
        // by the immediately following packet. This is not added to per-packet
        // jitter tolerance.
        _maxQpcPhaseShiftTicks = AudioPacketPositionMath.FramesToTimestampTicks(
            _maxDeviceGapFrames, _format.SampleRate, _timestampFrequency);
    }

    public bool IsStarted => Interlocked.Read(ref _anchorTimestamp) != 0;
    public long AnchorTimestamp => Interlocked.Read(ref _anchorTimestamp);
    public long MediaBytes => Interlocked.Read(ref _mediaBytes);
    public long LastDeviceEnd => Interlocked.Read(ref _lastDeviceEnd);
    public long QpcOutlierCount => Interlocked.Read(ref _qpcOutlierCount);
    public bool ContinuityDegraded => Volatile.Read(ref _continuityDegraded) != 0;
    internal long QpcJitterToleranceTicks => _qpcJitterToleranceTicks;
    internal long MaxDeviceGapFrames => _maxDeviceGapFrames;
    internal int ConfirmedQpcPhaseRebaseCount
        => checked((int)Interlocked.Read(ref _confirmedQpcPhaseRebaseCount));
    internal long CumulativeAbsoluteQpcPhaseAdjustmentTicks
        => Interlocked.Read(ref _cumulativeAbsoluteQpcPhaseAdjustmentTicks);
    internal int MaxConfirmedQpcPhaseRebases => MaxConfirmedQpcPhaseRebasesPerSession;
    internal long MaxCumulativeAbsoluteQpcPhaseAdjustmentTicks => _maxQpcPhaseShiftTicks;
    internal long CurrentQpcPhaseOffsetTicks
    {
        get
        {
            long trustedQpc = Interlocked.Read(ref _lastTrustedQpcTimestamp);
            long trustedDevice = Interlocked.Read(ref _lastTrustedDeviceStart);
            long referenceQpc = Interlocked.Read(ref _phaseBudgetReferenceQpcTimestamp);
            long referenceDevice = Interlocked.Read(ref _phaseBudgetReferenceDeviceStart);
            return trustedQpc < 0 || referenceQpc < 0
                ? 0
                : GetQpcDrift(trustedQpc, trustedDevice, referenceQpc, referenceDevice).DriftTicks;
        }
    }

    public void Start(long anchorTimestamp)
    {
        if (anchorTimestamp <= 0)
            throw new ArgumentOutOfRangeException(nameof(anchorTimestamp));
        if (Interlocked.CompareExchange(ref _anchorTimestamp, anchorTimestamp, 0) != 0)
            throw new InvalidOperationException("Loopback timeline has already started");
    }

    /// <summary>
    /// Re-anchors only the device/QPC epoch after the session has reopened the
    /// already-approved endpoint. The original wall-clock anchor and the
    /// current media cursor are intentionally preserved, so the next epoch
    /// starts exactly at the existing write cursor instead of rewriting old
    /// samples or treating a device-position reset as a media rollback.
    /// </summary>
    public void RebaseAfterEpochReset(long devicePosition, long packetStartTimestampTicks)
    {
        if (!IsStarted)
            throw new LoopbackTimelineException("Cannot rebase a loopback timeline before the successful Start boundary");
        if (devicePosition < 0 || packetStartTimestampTicks <= 0)
            throw new LoopbackTimelineException(
                $"Cannot rebase loopback timeline with invalid epoch evidence: device_position={devicePosition}; packet_start_ticks={packetStartTimestampTicks}");

        long mediaBytes = MediaBytes;
        Interlocked.Exchange(ref _anchorDevicePosition, devicePosition);
        Interlocked.Exchange(ref _anchorDeviceMediaBytes, mediaBytes);
        Interlocked.Exchange(ref _lastDeviceStart, devicePosition);
        Interlocked.Exchange(ref _lastDeviceEnd, devicePosition);
        Interlocked.Exchange(ref _lastPacketStartTimestamp, packetStartTimestampTicks);
        Interlocked.Exchange(ref _lastPacketEndTimestamp, packetStartTimestampTicks);
        Interlocked.Exchange(ref _lastTrustedQpcTimestamp, packetStartTimestampTicks);
        Interlocked.Exchange(ref _lastTrustedDeviceStart, devicePosition);
        // A real epoch recovery establishes a new within-epoch reference, but
        // lifetime phase-rebase count and absolute adjustment budget survive.
        Interlocked.Exchange(ref _phaseBudgetReferenceQpcTimestamp, packetStartTimestampTicks);
        Interlocked.Exchange(ref _phaseBudgetReferenceDeviceStart, devicePosition);
        Interlocked.Exchange(ref _pendingQpcPhaseTimestamp, -1);
        Interlocked.Exchange(ref _pendingQpcPhaseDeviceStart, -1);
        Volatile.Write(ref _consecutiveQpcOutliers, 0);
        Volatile.Write(ref _continuityDegraded, 1);
    }

    /// <summary>
    /// Advances the media cursor for zero samples already written by the
    /// session's objectively-measured recovery padding. The writer and this
    /// cursor must move together or the first packet of the new epoch could
    /// overlap the silence that was just inserted.
    /// </summary>
    public void AdvanceMediaCursor(long bytes)
    {
        if (bytes < 0 || bytes % _format.BlockAlign != 0)
            throw new ArgumentOutOfRangeException(nameof(bytes));
        if (bytes == 0)
            return;

        Interlocked.Add(ref _mediaBytes, bytes);
    }

    /// <summary>
    /// Appends a packet using both WASAPI position evidences. The writer
    /// callback receives a buffer, offset and count; only the non-overlapping
    /// packet suffix is written when a delayed packet overlaps prior padding.
    /// </summary>
    public LoopbackPacketAppendResult AppendPacket(
        byte[] buffer,
        int bytesRecorded,
        int framesRecorded,
        long devicePosition,
        long packetStartTimestampTicks,
        bool positionValid,
        Action<byte[], int, int> writePacket,
        Action<byte[], int> writeZeros,
        bool dataDiscontinuity = false)
    {
        if (!IsStarted)
            throw new LoopbackTimelineException("Loopback packet arrived before the successful Start boundary");
        if (!positionValid || devicePosition < 0 || packetStartTimestampTicks <= 0)
            throw new LoopbackTimelineException(
                $"WASAPI loopback packet position/QPC evidence is invalid: device_position={devicePosition}; packet_start_ticks={packetStartTimestampTicks}; packet_frames={framesRecorded}; position_valid={positionValid}; data_discontinuity={dataDiscontinuity}");
        if (buffer == null || bytesRecorded < 0 || bytesRecorded > buffer.Length ||
            bytesRecorded % _format.BlockAlign != 0 ||
            framesRecorded < 0 || framesRecorded * _format.BlockAlign != bytesRecorded)
            throw new LoopbackTimelineException("WASAPI loopback packet size is not block aligned");
        if (writePacket == null)
            throw new ArgumentNullException(nameof(writePacket));
        if (writeZeros == null)
            throw new ArgumentNullException(nameof(writeZeros));

        long packetDurationTicks = AudioPacketPositionMath.FramesToTimestampTicks(
            framesRecorded, _format.SampleRate, _timestampFrequency);
        long packetEndTimestamp;
        try { packetEndTimestamp = checked(packetStartTimestampTicks + packetDurationTicks); }
        catch (OverflowException)
        {
            throw new LoopbackTimelineException("WASAPI packet timestamp overflowed the media clock");
        }

        long previousDeviceStart = Interlocked.Read(ref _lastDeviceStart);
        long previousDeviceEnd = Interlocked.Read(ref _lastDeviceEnd);
        long previousPacketEnd = Interlocked.Read(ref _lastPacketEndTimestamp);
        long trustedQpc = Interlocked.Read(ref _lastTrustedQpcTimestamp);
        long trustedDeviceStart = Interlocked.Read(ref _lastTrustedDeviceStart);
        long currentDeviceGap = 0;
        bool deviceGapOutOfBounds = false;
        if (previousDeviceEnd >= 0)
        {
            if (devicePosition > previousDeviceEnd)
            {
                currentDeviceGap = checked(devicePosition - previousDeviceEnd);
                deviceGapOutOfBounds = currentDeviceGap > _maxDeviceGapFrames;
            }
            else if (devicePosition < previousDeviceStart)
            {
                throw new LoopbackTimelineException(
                    $"WASAPI loopback device position regressed without a legal overlap: device_position={devicePosition}; previous_device_start={previousDeviceStart}; previous_device_end={previousDeviceEnd}; current_device_gap_frames={currentDeviceGap}; packet_frames={framesRecorded}; position_valid={positionValid}; data_discontinuity={dataDiscontinuity}; qpc_outlier_count={QpcOutlierCount}");
            }
        }

        bool qpcOutlierAccepted = false;
        if (trustedQpc >= 0)
        {
            var trustedDrift = GetQpcDrift(packetStartTimestampTicks, devicePosition,
                trustedQpc, trustedDeviceStart);
            long pendingPhaseQpc = Interlocked.Read(ref _pendingQpcPhaseTimestamp);
            long pendingPhaseDevice = Interlocked.Read(ref _pendingQpcPhaseDeviceStart);

            if (pendingPhaseQpc > 0)
            {
                // A candidate phase is accepted only when the immediately next
                // packet is exactly contiguous in device frames and its QPC
                // delta follows that same device-frame delta within one frame
                // of timestamp quantization. Otherwise the old trusted track
                // must resume; a second unexplained point still fails closed.
                bool candidateDeviceContinuous = !dataDiscontinuity &&
                    !deviceGapOutOfBounds && previousDeviceEnd >= 0 &&
                    devicePosition == previousDeviceEnd &&
                    devicePosition >= pendingPhaseDevice;
                var phaseDrift = candidateDeviceContinuous
                    ? GetQpcDrift(packetStartTimestampTicks, devicePosition,
                        pendingPhaseQpc, pendingPhaseDevice)
                    : default;
                bool phaseTrajectoryMatches = candidateDeviceContinuous &&
                    IsWithinQpcJitter(phaseDrift.DriftTicks);
                bool originalTrajectoryMatches = IsWithinQpcJitter(trustedDrift.DriftTicks);
                if (!phaseTrajectoryMatches && !originalTrajectoryMatches)
                {
                    throw CreateQpcConflictException(
                        trustedDrift.QpcDeltaTicks,
                        trustedDrift.ExpectedQpcDeltaTicks,
                        trustedDrift.DriftTicks,
                        trustedDrift.DeviceDeltaFrames,
                        currentDeviceGap,
                        deviceGapOutOfBounds,
                        packetStartTimestampTicks,
                        previousPacketEnd,
                        framesRecorded,
                        positionValid,
                        dataDiscontinuity,
                        trustedQpc,
                        trustedDeviceStart);
                }

                bool confirmsNewPhase = phaseTrajectoryMatches && !originalTrajectoryMatches;
                if (confirmsNewPhase)
                {
                    long phaseReferenceQpc = Interlocked.Read(ref _phaseBudgetReferenceQpcTimestamp);
                    long phaseReferenceDevice = Interlocked.Read(ref _phaseBudgetReferenceDeviceStart);
                    var candidateStep = GetQpcDrift(pendingPhaseQpc, pendingPhaseDevice,
                        trustedQpc, trustedDeviceStart);
                    var candidateReferenceOffset = GetQpcDrift(pendingPhaseQpc, pendingPhaseDevice,
                        phaseReferenceQpc, phaseReferenceDevice);
                    long absoluteStepTicks = AbsoluteValue(candidateStep.DriftTicks);
                    long cumulativeTicks = Interlocked.Read(ref _cumulativeAbsoluteQpcPhaseAdjustmentTicks);
                    long rebaseCount = Interlocked.Read(ref _confirmedQpcPhaseRebaseCount);
                    bool phaseBudgetExceeded =
                        rebaseCount >= MaxConfirmedQpcPhaseRebasesPerSession ||
                        AbsoluteValue(candidateReferenceOffset.DriftTicks) > _maxQpcPhaseShiftTicks ||
                        absoluteStepTicks > _maxQpcPhaseShiftTicks - cumulativeTicks;
                    if (phaseBudgetExceeded)
                    {
                        string budgetDiagnostics =
                            $"; phase_budget_exceeded=True; confirmed_phase_rebases={rebaseCount}; " +
                            $"max_confirmed_phase_rebases={MaxConfirmedQpcPhaseRebasesPerSession}; " +
                            $"cumulative_absolute_phase_adjustment_ticks={cumulativeTicks}; " +
                            $"proposed_absolute_phase_adjustment_ticks={absoluteStepTicks}; " +
                            $"max_cumulative_absolute_phase_adjustment_ticks={_maxQpcPhaseShiftTicks}; " +
                            $"phase_reference_offset_ticks={candidateReferenceOffset.DriftTicks}";
                        throw CreateQpcConflictException(
                            trustedDrift.QpcDeltaTicks,
                            trustedDrift.ExpectedQpcDeltaTicks,
                            trustedDrift.DriftTicks,
                            trustedDrift.DeviceDeltaFrames,
                            currentDeviceGap,
                            deviceGapOutOfBounds,
                            packetStartTimestampTicks,
                            previousPacketEnd,
                            framesRecorded,
                            positionValid,
                            dataDiscontinuity,
                            trustedQpc,
                            trustedDeviceStart,
                            additionalDiagnostics: budgetDiagnostics);
                    }

                    Interlocked.Add(ref _cumulativeAbsoluteQpcPhaseAdjustmentTicks, absoluteStepTicks);
                    Interlocked.Increment(ref _confirmedQpcPhaseRebaseCount);
                }

                // The immediate successor confirmed either this bounded phase
                // or a return to the current trusted track. Only a genuinely
                // new phase spends lifetime budget. The device-frame media map
                // and the packet samples themselves are not altered.
                Interlocked.Exchange(ref _pendingQpcPhaseTimestamp, -1);
                Interlocked.Exchange(ref _pendingQpcPhaseDeviceStart, -1);
                Interlocked.Exchange(ref _lastTrustedQpcTimestamp, packetStartTimestampTicks);
                Interlocked.Exchange(ref _lastTrustedDeviceStart, devicePosition);
                Volatile.Write(ref _consecutiveQpcOutliers, 0);
            }
            else if (IsWithinQpcJitter(trustedDrift.DriftTicks))
            {
                Interlocked.Exchange(ref _lastTrustedQpcTimestamp, packetStartTimestampTicks);
                Interlocked.Exchange(ref _lastTrustedDeviceStart, devicePosition);
                Volatile.Write(ref _consecutiveQpcOutliers, 0);
            }
            else
            {
                // A first, very large QPC jump with otherwise usable device
                // evidence is an endpoint epoch-reset candidate. Do not write
                // this packet: CaptureSession will recover off the capture
                // thread, pad the measured hole, then rebase and accept the
                // next generation's packet in the correct order.
                bool epochResetCandidate =
                    _consecutiveQpcOutliers == 0 &&
                    !dataDiscontinuity &&
                    !deviceGapOutOfBounds &&
                    (trustedDrift.DriftTicks >= _epochResetQpcDriftThresholdTicks ||
                     trustedDrift.DriftTicks <= -_epochResetQpcDriftThresholdTicks);
                if (epochResetCandidate)
                {
                    Interlocked.Increment(ref _qpcOutlierCount);
                    Volatile.Write(ref _consecutiveQpcOutliers, 1);
                    Volatile.Write(ref _continuityDegraded, 1);
                    throw CreateQpcConflictException(
                        trustedDrift.QpcDeltaTicks,
                        trustedDrift.ExpectedQpcDeltaTicks,
                        trustedDrift.DriftTicks,
                        trustedDrift.DeviceDeltaFrames,
                        currentDeviceGap,
                        deviceGapOutOfBounds,
                        packetStartTimestampTicks,
                        previousPacketEnd,
                        framesRecorded,
                        positionValid,
                        dataDiscontinuity,
                        trustedQpc,
                        trustedDeviceStart,
                        epochResetCandidate: true);
                }

                if (_consecutiveQpcOutliers == 0 && !dataDiscontinuity &&
                    !deviceGapOutOfBounds)
                {
                    qpcOutlierAccepted = true;
                    Interlocked.Increment(ref _qpcOutlierCount);
                    Volatile.Write(ref _consecutiveQpcOutliers, 1);
                    Volatile.Write(ref _continuityDegraded, 1);

                    bool exactDeviceContinuity = previousDeviceEnd >= 0 &&
                        devicePosition == previousDeviceEnd;
                    if (exactDeviceContinuity &&
                        AbsoluteValue(trustedDrift.DriftTicks) <= _maxQpcPhaseShiftTicks)
                    {
                        Interlocked.Exchange(ref _pendingQpcPhaseTimestamp, packetStartTimestampTicks);
                        Interlocked.Exchange(ref _pendingQpcPhaseDeviceStart, devicePosition);
                    }
                }
                else
                {
                    throw CreateQpcConflictException(
                        trustedDrift.QpcDeltaTicks,
                        trustedDrift.ExpectedQpcDeltaTicks,
                        trustedDrift.DriftTicks,
                        trustedDrift.DeviceDeltaFrames,
                        currentDeviceGap,
                        deviceGapOutOfBounds,
                        packetStartTimestampTicks,
                        previousPacketEnd,
                        framesRecorded,
                        positionValid,
                        dataDiscontinuity,
                        trustedQpc,
                        trustedDeviceStart);
                }
            }
        }

        if (deviceGapOutOfBounds && !qpcOutlierAccepted)
        {
            throw new LoopbackTimelineException(
                $"WASAPI loopback device position discontinuity: device_gap_frames={currentDeviceGap}; max_gap_frames={_maxDeviceGapFrames}; packet_frames={framesRecorded}; position_valid={positionValid}; data_discontinuity={dataDiscontinuity}; last_trusted_qpc_ticks={trustedQpc}; last_trusted_device_start={trustedDeviceStart}; qpc_outlier_count={QpcOutlierCount}");
        }

        long anchor = AnchorTimestamp;
        long packetStartBytes;
        long leadingTrimBytes = 0;
        long anchorDevicePosition = Interlocked.Read(ref _anchorDevicePosition);
        if (anchorDevicePosition < 0)
        {
            long mediaStartTimestamp = Math.Max(anchor, packetStartTimestampTicks);
            packetStartBytes = TimestampToBytes(mediaStartTimestamp - anchor);
            if (packetStartTimestampTicks < anchor)
            {
                leadingTrimBytes = Math.Min(bytesRecorded,
                    TimestampToBytes(Math.Min(packetEndTimestamp, anchor) - packetStartTimestampTicks));
            }

            var leadingTrimFrames = leadingTrimBytes / _format.BlockAlign;
            Interlocked.Exchange(ref _anchorDevicePosition,
                checked(devicePosition + leadingTrimFrames));
            Interlocked.Exchange(ref _anchorDeviceMediaBytes, packetStartBytes);
            anchorDevicePosition = Interlocked.Read(ref _anchorDevicePosition);
        }
        else
        {
            long deviceOffsetFrames = checked(devicePosition - anchorDevicePosition);
            long deviceOffsetBytes = checked(deviceOffsetFrames * _format.BlockAlign);
            packetStartBytes = checked(Interlocked.Read(ref _anchorDeviceMediaBytes) + deviceOffsetBytes);
            if (packetStartBytes < 0)
                packetStartBytes = 0;
        }

        long currentBytes = MediaBytes;
        long zeroBytes = 0;
        if (packetStartBytes > currentBytes)
        {
            zeroBytes = WriteZerosAligned(packetStartBytes - currentBytes, writeZeros);
        }

        currentBytes = MediaBytes;
        long overlapBytes = leadingTrimBytes;
        if (currentBytes > packetStartBytes)
            overlapBytes = Math.Max(overlapBytes, currentBytes - packetStartBytes);

        long deviceOverlapFrames = previousDeviceEnd >= 0 && devicePosition < previousDeviceEnd
            ? Math.Min((long)framesRecorded, previousDeviceEnd - devicePosition)
            : 0;
        overlapBytes = Math.Max(overlapBytes, deviceOverlapFrames * _format.BlockAlign);
        overlapBytes = Math.Min(overlapBytes, bytesRecorded);
        overlapBytes -= overlapBytes % _format.BlockAlign;

        int writeOffset = checked((int)overlapBytes);
        int writeCount = bytesRecorded - writeOffset;
        if (writeCount > 0)
            writePacket(buffer, writeOffset, writeCount);

        Interlocked.Exchange(ref _mediaBytes, MediaBytes + writeCount);
        Interlocked.Exchange(ref _lastDeviceStart, devicePosition);
        Interlocked.Exchange(ref _lastDeviceEnd,
            Math.Max(previousDeviceEnd, checked(devicePosition + framesRecorded)));
        Interlocked.Exchange(ref _lastPacketStartTimestamp, packetStartTimestampTicks);
        Interlocked.Exchange(ref _lastPacketEndTimestamp,
            Math.Max(Interlocked.Read(ref _lastPacketEndTimestamp), packetEndTimestamp));

        if (trustedQpc < 0)
        {
            Interlocked.Exchange(ref _lastTrustedQpcTimestamp, packetStartTimestampTicks);
            Interlocked.Exchange(ref _lastTrustedDeviceStart, devicePosition);
            Interlocked.CompareExchange(ref _phaseBudgetReferenceQpcTimestamp, packetStartTimestampTicks, -1);
            Interlocked.CompareExchange(ref _phaseBudgetReferenceDeviceStart, devicePosition, -1);
            Volatile.Write(ref _consecutiveQpcOutliers, 0);
        }

        return new LoopbackPacketAppendResult(zeroBytes, writeCount, overlapBytes, qpcOutlierAccepted);
    }

    private (long DeviceDeltaFrames, long QpcDeltaTicks, long ExpectedQpcDeltaTicks, long DriftTicks)
        GetQpcDrift(long packetStartTimestamp, long devicePosition,
            long referenceQpcTimestamp, long referenceDevicePosition)
    {
        long deviceDeltaFrames = checked(devicePosition - referenceDevicePosition);
        long expectedQpcDeltaTicks = AudioPacketPositionMath.FramesToTimestampTicks(
            deviceDeltaFrames, _format.SampleRate, _timestampFrequency);
        long qpcDeltaTicks = checked(packetStartTimestamp - referenceQpcTimestamp);
        return (deviceDeltaFrames, qpcDeltaTicks, expectedQpcDeltaTicks,
            checked(qpcDeltaTicks - expectedQpcDeltaTicks));
    }

    private bool IsWithinQpcJitter(long driftTicks)
        => driftTicks >= -_qpcJitterToleranceTicks && driftTicks <= _qpcJitterToleranceTicks;

    private static long AbsoluteValue(long value)
        => value == long.MinValue ? long.MaxValue : Math.Abs(value);

    private LoopbackTimelineException CreateQpcConflictException(
        long qpcDeltaTicks,
        long expectedQpcDeltaTicks,
        long qpcDriftTicks,
        long deviceDeltaFrames,
        long currentDeviceGap,
        bool deviceGapOutOfBounds,
        long packetStartTimestampTicks,
        long previousPacketEnd,
        int framesRecorded,
        bool positionValid,
        bool dataDiscontinuity,
        long trustedQpc,
        long trustedDeviceStart,
        bool epochResetCandidate = false,
        string additionalDiagnostics = "")
    {
        return LoopbackTimelineException.QpcDevicePositionConflict(
            $"WASAPI loopback QPC/device position conflict: qpc_delta_ticks={qpcDeltaTicks}; expected_qpc_delta_ticks={expectedQpcDeltaTicks}; qpc_drift_ticks={qpcDriftTicks}; qpc_jitter_tolerance_ticks={_qpcJitterToleranceTicks}; previous_packet_end_ticks={previousPacketEnd}; packet_start_ticks={packetStartTimestampTicks}; device_delta_frames={deviceDeltaFrames}; current_device_gap_frames={currentDeviceGap}; max_device_gap_frames={_maxDeviceGapFrames}; device_gap_out_of_bounds={deviceGapOutOfBounds}; packet_frames={framesRecorded}; position_valid={positionValid}; data_discontinuity={dataDiscontinuity}; qpc_outlier_count={QpcOutlierCount}; consecutive_qpc_outliers={_consecutiveQpcOutliers}; last_trusted_qpc_ticks={trustedQpc}; last_trusted_device_start={trustedDeviceStart}; last_written_device_start={Interlocked.Read(ref _lastDeviceStart)}; last_written_device_end={Interlocked.Read(ref _lastDeviceEnd)}{additionalDiagnostics}",
            qpcDriftTicks,
            currentDeviceGap,
            deviceGapOutOfBounds,
            dataDiscontinuity,
            _consecutiveQpcOutliers,
            epochResetCandidate);
    }

    /// <summary>
    /// Only terminal finalization may advance a silent media timeline from the
    /// wall clock. Ordinary progress ticks deliberately return zero and leave
    /// the WAV cursor untouched.
    /// </summary>
    public long PadToTimestamp(long timestamp, Action<byte[], int> writeZeros, bool finalize = false)
    {
        if (writeZeros == null)
            throw new ArgumentNullException(nameof(writeZeros));

        if (!finalize)
            return 0;

        var anchor = AnchorTimestamp;
        if (anchor == 0)
            return 0;

        long targetBytes = timestamp > anchor ? TimestampToBytes(timestamp - anchor) : 0;
        long current = MediaBytes;
        long padBytes = targetBytes - current;
        if (finalize && padBytes <= 0 && current == 0)
            padBytes = _format.BlockAlign;
        if (padBytes <= 0)
            return 0;

        return WriteZerosAligned(padBytes, writeZeros);
    }

    public long MediaElapsedMs
        => (long)(MediaBytes / (double)_format.AverageBytesPerSecond * 1000.0);

    public long WallElapsedMs(long timestamp)
    {
        var anchor = AnchorTimestamp;
        return anchor == 0 || timestamp <= anchor
            ? 0
            : (long)((timestamp - anchor) / (double)_timestampFrequency * 1000.0);
    }

    private long WriteZerosAligned(long bytes, Action<byte[], int> writeZeros)
    {
        bytes = AlignDown(bytes);
        if (bytes <= 0)
            return 0;

        var zeroChunk = CreateZeroChunk();
        long remaining = bytes;
        while (remaining > 0)
        {
            int chunkBytes = (int)Math.Min(zeroChunk.Length, remaining);
            writeZeros(zeroChunk, chunkBytes);
            remaining -= chunkBytes;
        }

        Interlocked.Exchange(ref _mediaBytes, MediaBytes + bytes);
        return bytes;
    }

    private long TimestampToBytes(long elapsedTicks)
    {
        if (elapsedTicks <= 0)
            return 0;
        double bytes = elapsedTicks / (double)_timestampFrequency * _format.AverageBytesPerSecond;
        if (double.IsNaN(bytes) || double.IsInfinity(bytes) || bytes > long.MaxValue)
            throw new LoopbackTimelineException("Loopback timestamp could not be converted to bytes");
        return AlignDown((long)bytes);
    }

    private long AlignDown(long bytes)
        => bytes - bytes % _format.BlockAlign;

    private byte[] CreateZeroChunk()
    {
        int chunkBytes = Math.Min(MaxZeroChunkBytes, Math.Max(_format.BlockAlign,
            _format.BlockAlign * Math.Max(1, MaxZeroChunkBytes / _format.BlockAlign)));
        chunkBytes = Math.Max(_format.BlockAlign, chunkBytes - chunkBytes % _format.BlockAlign);
        return new byte[chunkBytes];
    }
}
