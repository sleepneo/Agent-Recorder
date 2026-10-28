using System.Reflection;
using NAudio.Wave;
using Xunit;

namespace AgentRecorder.AudioHelper.Tests;

public sealed class LoopbackClockConflictTests
{
    // These are minimal state constructions from the last failure summaries,
    // not replays of missing original packet streams. The arbitrary Start
    // anchor does not participate in the conflicting deltas under test.
    [Fact]
    public void QbObservedContinuousDeviceWithDiscontinuityStillFailsBeforeSuspectWriteAndExposesState()
    {
        var timeline = new LoopbackTimeline(new WaveFormat(48000, 32, 2), 10_000_000, TimeSpan.FromMilliseconds(100));
        timeline.Start(4_066_787_327_359);
        timeline.AppendPacket(new byte[480 * 8], 480 * 8, 480, 480, 4_066_787_327_359, true,
            (_, _, _) => { }, (_, _) => { });
        int writes = 0;
        var error = Assert.Throws<LoopbackTimelineException>(() => timeline.AppendPacket(
            new byte[480 * 8], 480 * 8, 480, 960, 4_066_790_691_436, true,
            (_, _, _) => writes++, (_, _) => writes++, dataDiscontinuity: true));
        Assert.True(error.IsQpcDevicePositionConflict);
        Assert.True(error.DataDiscontinuity);
        Assert.False(error.IsEpochResetCandidate);
        Assert.Equal(3_264_077, error.QpcDriftTicks);
        Assert.Equal(0, error.CurrentDeviceGapFrames);
        Assert.Equal(0, timeline.QpcOutlierCount);
        Assert.Equal(0, writes);
        Assert.NotNull(typeof(LoopbackTimeline).GetMethod("SnapshotForDiagnostics", BindingFlags.Instance | BindingFlags.NonPublic));
        var state = timeline.SnapshotForDiagnostics();
        Assert.Equal(480, state[4]); Assert.Equal(960, state[5]);
        Assert.Equal(4_066_787_327_359, state[6]); Assert.Equal(480, state[7]);
        Assert.Equal(-1, state[8]); Assert.Equal(0, state[10]);
    }

    [Fact]
    public void AirPodsObservedSecondConflictAfterOneOutlierFailsAndExposesPendingTrajectory()
    {
        var timeline = new LoopbackTimeline(new WaveFormat(44100, 16, 2), 10_000_000, TimeSpan.FromMilliseconds(100));
        timeline.Start(152_652_913_665);
        timeline.AppendPacket(new byte[441 * 4], 441 * 4, 441, 441, 152_652_913_665, true,
            (_, _, _) => { }, (_, _) => { });
        // Derived from previous_packet_end=152653214797 minus a 441-frame
        // duration (100000 ticks at this constructed frequency). Its flags
        // are not in the original summary; an eligible unflagged outlier is
        // the explicit reconstruction assumption consistent with count=1.
        var accepted = timeline.AppendPacket(new byte[441 * 4], 441 * 4, 441, 882, 152_653_114_797, true,
            (_, _, _) => { }, (_, _) => { });
        Assert.True(accepted.QpcOutlierAccepted);
        int writes = 0;
        var error = Assert.Throws<LoopbackTimelineException>(() => timeline.AppendPacket(
            new byte[441 * 4], 441 * 4, 441, 1323, 152_653_316_022, true,
            (_, _, _) => writes++, (_, _) => writes++));
        Assert.True(error.IsQpcDevicePositionConflict);
        Assert.False(error.DataDiscontinuity);
        Assert.False(error.IsEpochResetCandidate);
        Assert.Equal(202_357, error.QpcDriftTicks);
        Assert.Equal(1, error.ConsecutiveQpcOutliers);
        Assert.Equal(0, writes);
        Assert.NotNull(typeof(LoopbackTimeline).GetMethod("SnapshotForDiagnostics", BindingFlags.Instance | BindingFlags.NonPublic));
        var state = timeline.SnapshotForDiagnostics();
        Assert.Equal(882, state[4]); Assert.Equal(1323, state[5]);
        Assert.Equal(152_652_913_665, state[6]); Assert.Equal(441, state[7]);
        Assert.Equal(152_653_114_797, state[8]); Assert.Equal(882, state[9]);
        Assert.Equal(1, state[10]); Assert.Equal(1, state[11]);
    }
}
