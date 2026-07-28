using System;
using System.Linq;
using AgentRecorder.Capture;
using Xunit;

namespace AgentRecorder.Tests;

public class SilenceIntervalParserTests
{
    [Fact]
    public void Parse_EmptyStderr_ReturnsEmpty()
    {
        var intervals = SilenceIntervalParser.Parse("");
        Assert.Empty(intervals);
    }

    [Fact]
    public void Parse_WhitespaceStderr_ReturnsEmpty()
    {
        var intervals = SilenceIntervalParser.Parse("   \n\r  ");
        Assert.Empty(intervals);
    }

    [Fact]
    public void Parse_SingleInterval_ReturnsInterval()
    {
        var stderr = "[silencedetect @ 000001f8b6f9ee00] silence_start: 1.5\n" +
                     "[silencedetect @ 000001f8b6f9ee00] silence_end: 4.5 | silence_duration: 3\n";

        var intervals = SilenceIntervalParser.Parse(stderr);

        Assert.Single(intervals);
        Assert.Equal(1.5, intervals[0].Start);
        Assert.Equal(4.5, intervals[0].End);
        Assert.Equal(3.0, intervals[0].Duration);
    }

    [Fact]
    public void Parse_MultipleIntervals_ReturnsInOrder()
    {
        var stderr =
            "[silencedetect @ 000001] silence_start: 0.0\n" +
            "[silencedetect @ 000001] silence_end: 2.0 | silence_duration: 2.0\n" +
            "[silencedetect @ 000001] silence_start: 5.5\n" +
            "[silencedetect @ 000001] silence_end: 7.0 | silence_duration: 1.5\n";

        var intervals = SilenceIntervalParser.Parse(stderr);

        Assert.Equal(2, intervals.Count);
        Assert.Equal(0.0, intervals[0].Start);
        Assert.Equal(2.0, intervals[0].End);
        Assert.Equal(5.5, intervals[1].Start);
        Assert.Equal(7.0, intervals[1].End);
    }

    [Fact]
    public void Parse_UnmatchedStart_IsIgnored()
    {
        var stderr = "[silencedetect @ 000001] silence_start: 1.0\n";

        var intervals = SilenceIntervalParser.Parse(stderr);

        Assert.Empty(intervals);
    }

    [Fact]
    public void Parse_UnmatchedEnd_IsIgnored()
    {
        var stderr = "[silencedetect @ 000001] silence_end: 4.0 | silence_duration: 4.0\n";

        var intervals = SilenceIntervalParser.Parse(stderr);

        Assert.Empty(intervals);
    }

    [Fact]
    public void Classify_InitialSilence_IsInitial()
    {
        var intervals = new[] { new SilenceInterval(0.0, 3.0, 3.0) };
        var classification = SilenceIntervalParser.Classify(intervals, totalDurationSeconds: 10, internalThresholdSeconds: 3);

        Assert.Single(classification.Initial);
        Assert.Empty(classification.Internal);
        Assert.Empty(classification.Trailing);
    }

    [Fact]
    public void Classify_TrailingSilence_IsTrailing()
    {
        var intervals = new[] { new SilenceInterval(7.0, 10.0, 3.0) };
        var classification = SilenceIntervalParser.Classify(intervals, totalDurationSeconds: 10, internalThresholdSeconds: 3);

        Assert.Single(classification.Trailing);
        Assert.Empty(classification.Initial);
        Assert.Empty(classification.Internal);
    }

    [Fact]
    public void Classify_InternalLongSilence_IsInternal()
    {
        var intervals = new[] { new SilenceInterval(2.0, 6.0, 4.0) };
        var classification = SilenceIntervalParser.Classify(intervals, totalDurationSeconds: 10, internalThresholdSeconds: 3);

        Assert.Single(classification.Internal);
        Assert.Empty(classification.Initial);
        Assert.Empty(classification.Trailing);
        Assert.True(classification.HasInternalSilence);
        Assert.Equal(4.0, classification.LongestInternalSeconds);
    }

    [Fact]
    public void Classify_InternalShortSilence_BelowThreshold_IsNotInternal()
    {
        var intervals = new[] { new SilenceInterval(2.0, 4.5, 2.5) };
        var classification = SilenceIntervalParser.Classify(intervals, totalDurationSeconds: 10, internalThresholdSeconds: 3);

        Assert.Empty(classification.Internal);
        Assert.Empty(classification.Initial);
        Assert.Empty(classification.Trailing);
        Assert.False(classification.HasInternalSilence);
    }

    [Fact]
    public void Classify_MixedIntervals_ClassifiesCorrectly()
    {
        var intervals = new[]
        {
            new SilenceInterval(0.0, 1.0, 1.0),
            new SilenceInterval(3.0, 7.0, 4.0),
            new SilenceInterval(9.0, 10.0, 1.0)
        };

        var classification = SilenceIntervalParser.Classify(intervals, totalDurationSeconds: 10, internalThresholdSeconds: 3);

        Assert.Single(classification.Initial);
        Assert.Single(classification.Internal);
        Assert.Single(classification.Trailing);
        Assert.Equal(4.0, classification.LongestInternalSeconds);
    }

    [Fact]
    public void Classify_NearEdgeIntervals_AreTreatedAsEdge()
    {
        var intervals = new[]
        {
            new SilenceInterval(0.05, 1.0, 0.95),
            new SilenceInterval(9.0, 10.0, 1.0)
        };

        var classification = SilenceIntervalParser.Classify(intervals, totalDurationSeconds: 10, internalThresholdSeconds: 0.5);

        Assert.Equal(2, classification.Initial.Count + classification.Trailing.Count);
        Assert.Empty(classification.Internal);
    }
}
