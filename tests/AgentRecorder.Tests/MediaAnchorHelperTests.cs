using System;
using System.Diagnostics;
using AgentRecorder.Capture;
using Xunit;

namespace AgentRecorder.Tests;

public class MediaAnchorHelperTests
{
    [Fact]
    public void ToTimeSpan_HalfSecondOfStopwatchTicks_ReturnsFiveHundredMilliseconds()
    {
        var ticks = Stopwatch.Frequency / 2;
        var ts = MediaAnchorHelper.ToTimeSpan(ticks);
        Assert.Equal(TimeSpan.FromMilliseconds(500), ts);
    }

    [Fact]
    public void ToTimeSpan_DoesNotAssumeFrequencyMatchesTimeSpanTicksPerSecond()
    {
        // This test documents the contract: we divide by Stopwatch.Frequency,
        // not TimeSpan.TicksPerSecond. The helper must produce the correct
        // duration even on a machine where the two differ. When they happen to
        // be equal the two conversions coincide, so only assert inequality if
        // the frequencies actually differ.
        var ticks = Stopwatch.Frequency;
        var ts = MediaAnchorHelper.ToTimeSpan(ticks);
        Assert.Equal(TimeSpan.FromSeconds(1), ts);
        if (Stopwatch.Frequency != TimeSpan.TicksPerSecond)
            Assert.NotEqual(TimeSpan.FromTicks(ticks), ts);
    }

    [Fact]
    public void EstimateMediaStartAnchor_SubtractsOutTimeFromObservedTime()
    {
        var observed = Stopwatch.Frequency * 2; // 2 seconds
        var outTimeUs = 500_000L; // 0.5 seconds
        var anchor = MediaAnchorHelper.EstimateMediaStartAnchor(observed, outTimeUs);
        var preRoll = MediaAnchorHelper.ToTimeSpan(observed - anchor);
        Assert.Equal(TimeSpan.FromMilliseconds(500), preRoll);
    }

    [Fact]
    public void EstimateMediaStartAnchor_LateProgressWithAdvancedOutTime_DoesNotShiftAnchorForward()
    {
        // The progress arrives late, but the reported out_time_us already
        // accounts for the stream duration. The anchor must be based on
        // observed - out_time, not just the observed timestamp.
        var outTimeUs = 1_000_000L; // 1 second of media time
        var observed = Stopwatch.Frequency * 3; // observed at wall 3s
        var anchor = MediaAnchorHelper.EstimateMediaStartAnchor(observed, outTimeUs);
        Assert.Equal(Stopwatch.Frequency * 2, anchor); // media zero at 2s
    }

    [Fact]
    public void EstimateMediaStartAnchor_ZeroOutTime_ReturnsObservedTime()
    {
        var observed = Stopwatch.Frequency;
        var anchor = MediaAnchorHelper.EstimateMediaStartAnchor(observed, 0);
        Assert.Equal(observed, anchor);
    }

    [Theory]
    [InlineData(null)]
    [InlineData(0L)]
    [InlineData(-1L)]
    [InlineData(-1_000_000L)]
    public void TryEstimateMediaStartAnchor_InvalidOutTime_ReturnsFalseAndZeroAnchor(long? outTimeUs)
    {
        var observed = Stopwatch.Frequency;
        var ok = MediaAnchorHelper.TryEstimateMediaStartAnchor(observed, outTimeUs, out var anchor);
        Assert.False(ok);
        Assert.Equal(0L, anchor);
    }

    [Fact]
    public void TryEstimateMediaStartAnchor_PositiveOutTime_ReturnsTrueAndAnchor()
    {
        var observed = Stopwatch.Frequency * 2; // 2 seconds
        var outTimeUs = 500_000L; // 0.5 seconds
        var ok = MediaAnchorHelper.TryEstimateMediaStartAnchor(observed, outTimeUs, out var anchor);
        Assert.True(ok);
        var preRoll = MediaAnchorHelper.ToTimeSpan(observed - anchor);
        Assert.Equal(TimeSpan.FromMilliseconds(500), preRoll);
    }
}
