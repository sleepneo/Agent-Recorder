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
            new byte[2], 2, 1, 121_000, 1_100_000, true,
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
}
