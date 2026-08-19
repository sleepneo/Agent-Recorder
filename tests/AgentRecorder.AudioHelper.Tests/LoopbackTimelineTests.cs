using NAudio.Wave;
using Xunit;

namespace AgentRecorder.AudioHelper.Tests;

public sealed class LoopbackTimelineTests
{
    private static readonly WaveFormat Format = new(1000, 16, 1);
    private const long Frequency = 1_000_000;

    [Fact]
    public void SilentNoPacket_PadsWithBoundedChunksAndFinalizesToStopTimestamp()
    {
        var timeline = new LoopbackTimeline(Format, Frequency, TimeSpan.FromMilliseconds(100));
        var written = new List<byte[]>();
        timeline.Start(1_000_000);

        var firstPad = timeline.PadToTimestamp(3_000_000, (buffer, count) => written.Add(buffer[..count]));
        var finalPad = timeline.PadToTimestamp(4_000_000, (buffer, count) => written.Add(buffer[..count]), finalize: true);

        Assert.Equal(0, firstPad);
        Assert.Equal(6000, finalPad);
        Assert.Equal(6000, timeline.MediaBytes);
        Assert.NotEmpty(written);
        Assert.All(written.SelectMany(bytes => bytes), value => Assert.Equal(0, value));
        Assert.All(written, bytes => Assert.True(bytes.Length <= 65536));
    }

    [Fact]
    public void DelayedPacketBeyondMultipleTicks_PreservesRealBytesWithoutDoubleCounting()
    {
        var timeline = new LoopbackTimeline(Format, Frequency, TimeSpan.FromMilliseconds(100));
        timeline.Start(1_000_000);

        // Multiple ordinary ticks may observe wall time, but must not write
        // silence or move the media cursor before this delayed real packet.
        timeline.PadToTimestamp(2_500_000, (_, _) => throw new Xunit.Sdk.XunitException("ordinary tick wrote media"));
        timeline.PadToTimestamp(3_000_000, (_, _) => throw new Xunit.Sdk.XunitException("ordinary tick wrote media"));
        var packet = Enumerable.Repeat((byte)0x5A, 200).ToArray();
        var output = new List<byte>();
        var result = timeline.AppendPacket(packet, packet.Length, 100, 0, 1_200_000, true,
            (buffer, offset, count) => output.AddRange(buffer.AsSpan(offset, count).ToArray()),
            (buffer, count) => output.AddRange(buffer.AsSpan(0, count).ToArray()));

        Assert.Equal(400, result.ZeroBytesWritten);
        Assert.Equal(packet.Length, result.PacketBytesWritten);
        Assert.Equal(0, result.PacketBytesSkipped);
        Assert.Equal(400, output.Take(400).Count(value => value == 0));
        Assert.Equal(packet, output.Skip(400).Take(packet.Length).ToArray());

        timeline.PadToTimestamp(3_000_000, (_, _) => { }, finalize: true);
        Assert.Equal(4000, timeline.MediaBytes);
        Assert.Equal(2000, timeline.MediaElapsedMs);
        Assert.Equal(2000, timeline.WallElapsedMs(3_000_000));
    }

    [Fact]
    public void SilentDelayedRealPacketThenSilent_PreservesPacketIntervalOrder()
    {
        var timeline = new LoopbackTimeline(Format, Frequency, TimeSpan.FromMilliseconds(100));
        timeline.Start(1_000_000);

        var packet = Enumerable.Repeat((byte)0x33, 200).ToArray(); // 0.2s at this format
        var output = new List<byte>();
        var result = timeline.AppendPacket(packet, packet.Length, 100, 0, 1_900_000, true,
            (buffer, offset, count) => output.AddRange(buffer.AsSpan(offset, count).ToArray()),
            (buffer, count) => output.AddRange(buffer.AsSpan(0, count).ToArray()));
        timeline.PadToTimestamp(3_000_000, (buffer, count) => output.AddRange(buffer.AsSpan(0, count).ToArray()), finalize: true);

        Assert.Equal(200, result.PacketBytesWritten);
        Assert.Equal(1800, output.Take(1800).Count(value => value == 0));
        Assert.Equal(packet, output.Skip(1800).Take(packet.Length).ToArray());
        Assert.All(output.Skip(2000), value => Assert.Equal(0, value));
        Assert.Equal(4000, timeline.MediaBytes);
        Assert.Equal(2000, timeline.MediaElapsedMs);
    }

    [Fact]
    public void OverlappingPositionedPackets_SkipOnlyConfirmedDuplicateFrames()
    {
        var timeline = new LoopbackTimeline(Format, Frequency, TimeSpan.Zero);
        timeline.Start(1_000_000);
        var first = Enumerable.Repeat((byte)0x11, 200).ToArray();
        var second = Enumerable.Repeat((byte)0x22, 200).ToArray();
        var output = new List<byte>();

        timeline.AppendPacket(first, first.Length, 100, 100, 1_200_000, true,
            (buffer, offset, count) => output.AddRange(buffer.AsSpan(offset, count).ToArray()),
            (buffer, count) => output.AddRange(buffer.AsSpan(0, count).ToArray()));
        var result = timeline.AppendPacket(second, second.Length, 100, 150, 1_250_000, true,
            (buffer, offset, count) => output.AddRange(buffer.AsSpan(offset, count).ToArray()),
            (buffer, count) => output.AddRange(buffer.AsSpan(0, count).ToArray()));

        Assert.Equal(100, result.PacketBytesSkipped);
        Assert.Equal(100, result.PacketBytesWritten);
        var expected = new byte[400].Concat(first).Concat(second.Skip(100)).ToArray();
        Assert.Equal(expected, output.ToArray());
        Assert.Equal(700, timeline.MediaBytes);
    }

    [Fact]
    public void ContinuousDevicePosition_AllowsQpcStartSlightlyBeforePreviousEstimatedEnd()
    {
        var timeline = new LoopbackTimeline(Format, Frequency, TimeSpan.Zero);
        timeline.Start(1_000_000);
        var first = Enumerable.Repeat((byte)0x11, 200).ToArray();
        var second = Enumerable.Repeat((byte)0x22, 200).ToArray();
        var output = new List<byte>();

        timeline.AppendPacket(first, first.Length, 100, 0, 1_000_000, true,
            (buffer, offset, count) => output.AddRange(buffer.AsSpan(offset, count).ToArray()),
            (buffer, count) => output.AddRange(buffer.AsSpan(0, count).ToArray()));
        var result = timeline.AppendPacket(second, second.Length, 100, 100, 1_099_000, true,
            (buffer, offset, count) => output.AddRange(buffer.AsSpan(offset, count).ToArray()),
            (buffer, count) => output.AddRange(buffer.AsSpan(0, count).ToArray()));

        Assert.Equal(0, result.PacketBytesSkipped);
        Assert.Equal(200, result.PacketBytesWritten);
        Assert.Equal(first.Concat(second).ToArray(), output.ToArray());
    }

    [Fact]
    public void ContinuousDevicePosition_AllowsOneFrameQpcQuantizationJitter()
    {
        var timeline = new LoopbackTimeline(Format, Frequency, TimeSpan.Zero);
        timeline.Start(1_000_000);
        timeline.AppendPacket(new byte[200], 200, 100, 0, 1_000_000, true,
            (_, _, _) => { }, (_, _) => { });

        var result = timeline.AppendPacket(new byte[200], 200, 100, 100, 1_100_500, true,
            (_, _, _) => { }, (_, _) => { });

        Assert.Equal(200, result.PacketBytesWritten);
    }

    [Fact]
    public void LegalDeviceOverlap_WithQpcJitter_SkipsOnlyConfirmedFrames()
    {
        var timeline = new LoopbackTimeline(Format, Frequency, TimeSpan.Zero);
        timeline.Start(1_000_000);
        timeline.AppendPacket(new byte[200], 200, 100, 100, 1_000_000, true,
            (_, _, _) => { }, (_, _) => { });

        var result = timeline.AppendPacket(new byte[200], 200, 100, 150, 1_050_500, true,
            (_, _, _) => { }, (_, _) => { });

        Assert.Equal(100, result.PacketBytesSkipped);
        Assert.Equal(100, result.PacketBytesWritten);
    }

    [Fact]
    public void ContinuousDevicePosition_TwoConsecutiveQpcOutliersFailClosedWithDiagnostics()
    {
        var timeline = new LoopbackTimeline(Format, Frequency, TimeSpan.FromMilliseconds(100));
        timeline.Start(1_000_000);
        timeline.AppendPacket(new byte[200], 200, 100, 0, 1_000_000, true,
            (_, _, _) => { }, (_, _) => { });

        var firstOutlier = timeline.AppendPacket(new byte[200], 200, 100, 100, 1_050_000, true,
            (_, _, _) => { }, (_, _) => { });
        Assert.True(firstOutlier.QpcOutlierAccepted);

        var ex = Assert.Throws<LoopbackTimelineException>(() => timeline.AppendPacket(
            new byte[200], 200, 100, 200, 1_060_000, true,
            (_, _, _) => { }, (_, _) => { }));

        Assert.Contains("qpc_delta_ticks=", ex.Message);
        Assert.Contains("qpc_drift_ticks=", ex.Message);
        Assert.Contains("qpc_jitter_tolerance_ticks=", ex.Message);
        Assert.Contains("qpc_outlier_count=1", ex.Message);
        Assert.Contains("last_trusted_qpc_ticks=1000000", ex.Message);
        Assert.Contains("last_trusted_device_start=0", ex.Message);
        Assert.Contains("current_device_gap_frames=0", ex.Message);
    }

    [Fact]
    public void DevicePositionClearlyBackwardsWithoutOverlap_FailsClosedWithDiagnostics()
    {
        var timeline = new LoopbackTimeline(Format, Frequency, TimeSpan.FromMilliseconds(100));
        timeline.Start(1_000_000);
        timeline.AppendPacket(new byte[200], 200, 100, 100, 1_000_000, true,
            (_, _, _) => { }, (_, _) => { });

        var ex = Assert.Throws<LoopbackTimelineException>(() => timeline.AppendPacket(
            new byte[200], 200, 100, 0, 900_000, true,
            (_, _, _) => { }, (_, _) => { }));

        Assert.Contains("device position regressed", ex.Message);
        Assert.Contains("previous_device_start=100", ex.Message);
    }

    [Fact]
    public void DeviceGapAndQpcGapAgree_PadsConfirmedFramesBeforePacket()
    {
        var timeline = new LoopbackTimeline(Format, Frequency, TimeSpan.FromMilliseconds(100));
        timeline.Start(1_000_000);
        var output = new List<byte>();
        timeline.AppendPacket(new byte[200], 200, 100, 0, 1_000_000, true,
            (buffer, offset, count) => output.AddRange(buffer.AsSpan(offset, count).ToArray()),
            (buffer, count) => output.AddRange(buffer.AsSpan(0, count).ToArray()));

        var result = timeline.AppendPacket(Enumerable.Repeat((byte)0x44, 200).ToArray(), 200, 100, 200, 1_200_000, true,
            (buffer, offset, count) => output.AddRange(buffer.AsSpan(offset, count).ToArray()),
            (buffer, count) => output.AddRange(buffer.AsSpan(0, count).ToArray()));

        Assert.Equal(200, result.ZeroBytesWritten);
        Assert.Equal(200, result.PacketBytesWritten);
        Assert.Equal(Enumerable.Repeat((byte)0, 200).Concat(new byte[200]).Concat(Enumerable.Repeat((byte)0x44, 200)).ToArray(), output.ToArray());
    }

    [Fact]
    public void DeviceGapAndQpcGapConflictBeyondPaddingBoundary_FailsClosed()
    {
        var timeline = new LoopbackTimeline(Format, Frequency, TimeSpan.FromMilliseconds(100));
        timeline.Start(1_000_000);
        timeline.AppendPacket(new byte[200], 200, 100, 0, 1_000_000, true,
            (_, _, _) => { }, (_, _) => { });

        var ex = Assert.Throws<LoopbackTimelineException>(() => timeline.AppendPacket(
            new byte[200], 200, 100, 300, 1_050_000, true,
            (_, _, _) => { }, (_, _) => { }));

        Assert.Contains("QPC/device position conflict", ex.Message);
        Assert.Contains("device_delta_frames=300", ex.Message);
        Assert.Contains("current_device_gap_frames=200", ex.Message);
        Assert.Contains("max_device_gap_frames=100", ex.Message);
    }

    [Fact]
    public void DataDiscontinuityWithConsistentPositions_IsAcceptedAndPreservesEvidencePath()
    {
        var timeline = new LoopbackTimeline(Format, Frequency, TimeSpan.FromMilliseconds(100));
        timeline.Start(1_000_000);
        timeline.AppendPacket(new byte[200], 200, 100, 0, 1_000_000, true,
            (_, _, _) => { }, (_, _) => { });

        var result = timeline.AppendPacket(new byte[200], 200, 100, 100, 1_100_000, true,
            (_, _, _) => { }, (_, _) => { }, dataDiscontinuity: true);

        Assert.Equal(200, result.PacketBytesWritten);
    }

    [Fact]
    public void InvalidPacketPosition_FailsClosed()
    {
        var timeline = new LoopbackTimeline(Format, Frequency, TimeSpan.Zero);
        timeline.Start(1_000_000);

        var ex = Assert.Throws<LoopbackTimelineException>(() => timeline.AppendPacket(
            new byte[20], 20, 10, 0, 1_000_000, false,
            (_, _, _) => { }, (_, _) => { }));

        Assert.Contains("position/QPC", ex.Message);
    }

    [Fact]
    public void LargeDevicePositionDiscontinuity_FailsClosedWithEvidence()
    {
        var timeline = new LoopbackTimeline(Format, Frequency, TimeSpan.Zero);
        timeline.Start(1_000_000);
        timeline.AppendPacket(new byte[2], 2, 1, 0, 1_000_000, true,
            (_, _, _) => { }, (_, _) => { });

        var ex = Assert.Throws<LoopbackTimelineException>(() => timeline.AppendPacket(
            new byte[2], 2, 1, 121_000, 122_000_000, true,
            (_, _, _) => { }, (_, _) => { }));

        Assert.Contains("discontinuity", ex.Message);
    }

    [Fact]
    public void FinalizeWithoutAnyPacket_ProducesAtLeastOneAlignedBlock()
    {
        var timeline = new LoopbackTimeline(Format, Frequency, TimeSpan.Zero);
        var zeroBytes = 0;

        timeline.Start(10);
        var padded = timeline.PadToTimestamp(10, (_, count) => zeroBytes += count, finalize: true);

        Assert.Equal(Format.BlockAlign, padded);
        Assert.Equal(Format.BlockAlign, zeroBytes);
        Assert.Equal(Format.BlockAlign, timeline.MediaBytes);
    }

    [Fact]
    public void FortyEightKhz_OneQpcOutlier_UsesDevicePositionAndFillsOnlyTheProvenGap()
    {
        var format = new WaveFormat(48_000, 16, 1);
        const long frequency = 10_000_000;
        var timeline = new LoopbackTimeline(format, frequency, TimeSpan.FromMilliseconds(100));
        var output = new List<byte>();
        timeline.Start(10_000_000);

        var first = Enumerable.Repeat((byte)0x11, 960).ToArray();
        var outlier = Enumerable.Repeat((byte)0x22, 960).ToArray();
        timeline.AppendPacket(first, first.Length, 480, 0, 10_000_000, true,
            (buffer, offset, count) => output.AddRange(buffer.AsSpan(offset, count).ToArray()),
            (buffer, count) => output.AddRange(buffer.AsSpan(0, count).ToArray()));

        var result = timeline.AppendPacket(outlier, outlier.Length, 480, 960, 11_364_385, true,
            (buffer, offset, count) => output.AddRange(buffer.AsSpan(offset, count).ToArray()),
            (buffer, count) => output.AddRange(buffer.AsSpan(0, count).ToArray()));

        Assert.True(result.QpcOutlierAccepted);
        Assert.Equal(960, result.ZeroBytesWritten); // 480 missing frames, not 10ms of wall time.
        Assert.Equal(960, result.PacketBytesWritten);
        Assert.Equal(1, timeline.QpcOutlierCount);
        Assert.True(timeline.ContinuityDegraded);
        Assert.Equal(first.Concat(new byte[960]).Concat(outlier).ToArray(), output.ToArray());
    }

    [Fact]
    public void FortyEightKhz_NextPacketRecoversAgainstTrustedQpcAndPreservesExactOrder()
    {
        var format = new WaveFormat(48_000, 16, 1);
        const long frequency = 10_000_000;
        var timeline = new LoopbackTimeline(format, frequency, TimeSpan.FromMilliseconds(100));
        var output = new List<byte>();
        timeline.Start(10_000_000);

        void Append(byte value, long devicePosition, long qpc)
        {
            var packet = Enumerable.Repeat(value, 960).ToArray();
            timeline.AppendPacket(packet, packet.Length, 480, devicePosition, qpc, true,
                (buffer, offset, count) => output.AddRange(buffer.AsSpan(offset, count).ToArray()),
                (buffer, count) => output.AddRange(buffer.AsSpan(0, count).ToArray()));
        }

        Append(0x11, 0, 10_000_000);
        var recovered = timeline.AppendPacket(
            Enumerable.Repeat((byte)0x22, 960).ToArray(), 960, 480, 960, 11_364_385, true,
            (buffer, offset, count) => output.AddRange(buffer.AsSpan(offset, count).ToArray()),
            (buffer, count) => output.AddRange(buffer.AsSpan(0, count).ToArray()));
        Append(0x33, 1_440, 10_300_000);

        Assert.True(recovered.QpcOutlierAccepted);
        Assert.Equal(1, timeline.QpcOutlierCount);
        var expected = Enumerable.Repeat((byte)0x11, 960)
            .Concat(new byte[960])
            .Concat(Enumerable.Repeat((byte)0x22, 960))
            .Concat(Enumerable.Repeat((byte)0x33, 960))
            .ToArray();
        Assert.Equal(expected, output.ToArray());
        Assert.Equal(expected.Length, timeline.MediaBytes);
    }

    [Fact]
    public void FortyEightKhz_SecondUntrustedQpcFailsWithTrustedBaselineDiagnostics()
    {
        var format = new WaveFormat(48_000, 16, 1);
        const long frequency = 10_000_000;
        var timeline = new LoopbackTimeline(format, frequency, TimeSpan.FromMilliseconds(100));
        timeline.Start(10_000_000);
        timeline.AppendPacket(new byte[960], 960, 480, 0, 10_000_000, true,
            (_, _, _) => { }, (_, _) => { });
        timeline.AppendPacket(new byte[960], 960, 480, 960, 11_364_385, true,
            (_, _, _) => { }, (_, _) => { });

        var ex = Assert.Throws<LoopbackTimelineException>(() => timeline.AppendPacket(
            new byte[960], 960, 480, 1_440, 11_464_385, true,
            (_, _, _) => { }, (_, _) => { }));

        Assert.Contains("qpc_outlier_count=1", ex.Message);
        Assert.Contains("consecutive_qpc_outliers=1", ex.Message);
        Assert.Contains("last_trusted_qpc_ticks=10000000", ex.Message);
        Assert.Contains("last_trusted_device_start=0", ex.Message);
        Assert.Contains("current_device_gap_frames=0", ex.Message);
        Assert.Contains("packet_frames=480", ex.Message);
        Assert.Contains("position_valid=True", ex.Message);
    }

    [Fact]
    public void FortyEightKhz_OverBoundaryDeviceGapAndQpcConflict_FailsClosed()
    {
        var format = new WaveFormat(48_000, 16, 1);
        const long frequency = 10_000_000;
        var timeline = new LoopbackTimeline(format, frequency, TimeSpan.FromMilliseconds(100));
        timeline.Start(10_000_000);
        timeline.AppendPacket(new byte[960], 960, 480, 0, 10_000_000, true,
            (_, _, _) => { }, (_, _) => { });

        var ex = Assert.Throws<LoopbackTimelineException>(() => timeline.AppendPacket(
            new byte[960], 960, 480, 5_281, 11_364_385, true,
            (_, _, _) => { }, (_, _) => { }));

        Assert.Contains("QPC/device position conflict", ex.Message);
        Assert.Contains("current_device_gap_frames=4801", ex.Message);
        Assert.Contains("max_device_gap_frames=4800", ex.Message);
        Assert.Contains("device_gap_out_of_bounds=True", ex.Message);
    }

    [Fact]
    public void FortyEightKhz_PositionRegressionAndTimestampErrorEquivalentFailClosed()
    {
        var format = new WaveFormat(48_000, 16, 1);
        var timeline = new LoopbackTimeline(format, 10_000_000, TimeSpan.FromMilliseconds(100));
        timeline.Start(10_000_000);
        timeline.AppendPacket(new byte[960], 960, 480, 480, 10_100_000, true,
            (_, _, _) => { }, (_, _) => { });

        var regression = Assert.Throws<LoopbackTimelineException>(() => timeline.AppendPacket(
            new byte[960], 960, 480, 0, 10_200_000, true,
            (_, _, _) => { }, (_, _) => { }));
        Assert.Contains("device position regressed", regression.Message);

        var invalid = Assert.Throws<LoopbackTimelineException>(() => timeline.AppendPacket(
            new byte[960], 960, 480, 960, 10_200_000, false,
            (_, _, _) => { }, (_, _) => { }));
        Assert.Contains("position_valid=False", invalid.Message);
        Assert.Contains("packet_frames=480", invalid.Message);
    }
}
