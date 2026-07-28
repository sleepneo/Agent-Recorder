using System.Collections.Generic;
using AgentRecorder.Capture;
using Xunit;

namespace AgentRecorder.Tests;

public class FFmpegProgressParserTests
{
    [Fact]
    public void CompleteGroup_FrameOneAndPositiveSize_ProducesObservation()
    {
        var parser = new FFmpegProgressParser();
        FirstFrameObservation? observed = null;
        parser.GroupCompleted += g =>
        {
            if (g.HasFirstFrameEvidence)
                observed = new FirstFrameObservation
                {
                    FrameNumber = g.Frame,
                    TotalSizeBytes = g.TotalSize,
                    OutTimeUs = g.OutTimeUs
                };
        };

        parser.FeedText("frame=1\nfps=30\nstream_0_0_q=0.5\nbitrate=100.5kbits/s\ntotal_size=1234\nout_time_us=0\nout_time_ms=0\nout_time=00:00:00.000000\ndup_frames=0\ndrop_frames=0\nspeed=1.00x\nprogress=continue");

        Assert.NotNull(observed);
        Assert.Equal(1, observed!.FrameNumber);
        Assert.Equal(1234, observed.TotalSizeBytes);
        Assert.Equal(0, observed.OutTimeUs);
    }

    [Fact]
    public void CompleteGroup_FrameOneAndPositiveSize_ProgressEnd_ProducesObservation()
    {
        var parser = new FFmpegProgressParser();
        FirstFrameObservation? observed = null;
        parser.GroupCompleted += g =>
        {
            if (g.HasFirstFrameEvidence)
                observed = new FirstFrameObservation
                {
                    FrameNumber = g.Frame,
                    TotalSizeBytes = g.TotalSize
                };
        };

        parser.FeedText("frame=1\ntotal_size=1\nprogress=end");

        Assert.NotNull(observed);
        Assert.Equal(1, observed!.FrameNumber);
        Assert.Equal(1, observed.TotalSizeBytes);
    }

    [Theory]
    [InlineData("frame=0\ntotal_size=100\nprogress=continue")]
    [InlineData("frame=1\ntotal_size=0\nprogress=continue")]
    [InlineData("frame=1\ntotal_size=100\nprogress=unknown")]
    [InlineData("frame=1\ntotal_size=100")]
    [InlineData("frame=1")]
    [InlineData("total_size=100")]
    public void IncompleteOrInvalidGroup_DoesNotProduceObservation(string text)
    {
        var parser = new FFmpegProgressParser();
        var observed = false;
        parser.GroupCompleted += g =>
        {
            if (g.HasFirstFrameEvidence)
                observed = true;
        };

        parser.FeedText(text);

        Assert.False(observed);
    }

    [Fact]
    public void UnknownFieldsAndMultipleGroups_PublishesAllCompletedGroups()
    {
        var parser = new FFmpegProgressParser();
        var observations = new List<FirstFrameObservation>();
        parser.GroupCompleted += g =>
        {
            if (g.HasFirstFrameEvidence)
                observations.Add(new FirstFrameObservation
                {
                    FrameNumber = g.Frame,
                    TotalSizeBytes = g.TotalSize
                });
        };

        parser.FeedText(@"
frame=0
unknown_field=hello
progress=continue

frame=1
total_size=100
progress=continue

frame=2
total_size=200
progress=continue

frame=10
total_size=999
progress=end
");

        Assert.Equal(3, observations.Count);
        Assert.Equal(1, observations[0].FrameNumber);
        Assert.Equal(100, observations[0].TotalSizeBytes);
        Assert.Equal(2, observations[1].FrameNumber);
        Assert.Equal(200, observations[1].TotalSizeBytes);
        Assert.Equal(10, observations[2].FrameNumber);
        Assert.Equal(999, observations[2].TotalSizeBytes);
    }

    [Fact]
    public void MultipleQualifiedGroups_AllPublishedForAnchorTracking()
    {
        var parser = new FFmpegProgressParser();
        var count = 0;
        parser.GroupCompleted += g =>
        {
            if (g.HasFirstFrameEvidence)
                System.Threading.Interlocked.Increment(ref count);
        };

        parser.FeedText("frame=1\ntotal_size=100\nprogress=continue\nframe=2\ntotal_size=200\nprogress=continue");

        Assert.Equal(2, count);
    }

    [Fact]
    public void ParserSwallowsObserverException()
    {
        var parser = new FFmpegProgressParser();
        parser.GroupCompleted += _ => throw new System.InvalidOperationException("boom");

        var ex = Record.Exception(() => parser.FeedText("frame=1\ntotal_size=100\nprogress=continue"));

        Assert.Null(ex);
    }

    [Fact]
    public void IllegalNumericValues_AreIgnored()
    {
        var parser = new FFmpegProgressParser();
        FirstFrameObservation? observed = null;
        parser.GroupCompleted += g =>
        {
            if (g.HasFirstFrameEvidence)
                observed = new FirstFrameObservation { FrameNumber = g.Frame, TotalSizeBytes = g.TotalSize };
        };

        parser.FeedText("frame=not_a_number\ntotal_size=100\nprogress=continue");

        Assert.Null(observed);

        parser.FeedText("frame=1\ntotal_size=-5\nprogress=continue");

        Assert.Null(observed);
    }

    [Fact]
    public void OutTimeUs_Missing_IsNull()
    {
        var parser = new FFmpegProgressParser();
        long? outTimeUs = -1;
        parser.GroupCompleted += g =>
        {
            if (g.HasFirstFrameEvidence)
                outTimeUs = g.OutTimeUs;
        };

        parser.FeedText("frame=1\ntotal_size=100\nprogress=continue");

        Assert.Null(outTimeUs);
    }

    [Fact]
    public void FeedLine_Null_FlushesCurrentGroup()
    {
        var parser = new FFmpegProgressParser();
        FirstFrameObservation? observed = null;
        parser.GroupCompleted += g =>
        {
            if (g.HasFirstFrameEvidence)
                observed = new FirstFrameObservation { FrameNumber = g.Frame, TotalSizeBytes = g.TotalSize };
        };

        parser.FeedLine("frame=1");
        parser.FeedLine("total_size=100");
        parser.FeedLine("progress=continue");
        parser.FeedLine(null!);

        Assert.NotNull(observed);
    }

}
