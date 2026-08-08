#include "test_framework.h"

#include "timeline.h"

using namespace wgc;

namespace {

constexpr int64_t kHnsPerSecond = 10'000'000LL;

} // namespace

TEST_REGISTRAR(FrameTimelineFirstFrameIsZero, []() {
    FrameTimeline timeline(30);
    int64_t mediaTime = -1;
    int64_t duration = -1;
    ASSERT_TRUE(timeline.SubmitFrame(kHnsPerSecond, &mediaTime, &duration));
    ASSERT_EQ(mediaTime, 0);
    ASSERT_EQ(duration, kHnsPerSecond / 30);
});

TEST_REGISTRAR(FrameTimelinePreservesIntervals, []() {
    FrameTimeline timeline(30);
    int64_t mediaTime = 0;
    int64_t duration = 0;
    ASSERT_TRUE(timeline.SubmitFrame(kHnsPerSecond, &mediaTime, &duration));
    ASSERT_TRUE(timeline.SubmitFrame(kHnsPerSecond + 333'333LL, &mediaTime, &duration));
    ASSERT_EQ(mediaTime, 333'333LL);
    ASSERT_EQ(duration, 333'333LL);
});

TEST_REGISTRAR(FrameTimelineFinalizesLastSampleAtCaptureEnd, []() {
    FrameTimeline timeline(30);
    int64_t mediaTime = 0;
    int64_t duration = 0;
    ASSERT_TRUE(timeline.SubmitFrame(kHnsPerSecond, &mediaTime, &duration));
    ASSERT_TRUE(timeline.SubmitFrame(kHnsPerSecond + 91'750'000LL, &mediaTime, &duration));

    ASSERT_TRUE(timeline.FinalizeAt(100'080'000LL, &mediaTime, &duration));
    ASSERT_EQ(mediaTime, 91'750'000LL);
    ASSERT_EQ(duration, 8'330'000LL);
    ASSERT_EQ(mediaTime + duration, 100'080'000LL);
});

TEST_REGISTRAR(FrameTimelineDropsNonMonotonic, []() {
    FrameTimeline timeline(30);
    int64_t mediaTime = 0;
    int64_t duration = 0;
    ASSERT_TRUE(timeline.SubmitFrame(kHnsPerSecond, &mediaTime, &duration));
    ASSERT_FALSE(timeline.SubmitFrame(kHnsPerSecond - 1, &mediaTime, &duration));
    ASSERT_FALSE(timeline.SubmitFrame(kHnsPerSecond, &mediaTime, &duration));
    ASSERT_EQ(timeline.FramesDropped(), 2);
});

TEST_REGISTRAR(FrameTimelineDropsNegativeTime, []() {
    FrameTimeline timeline(30);
    int64_t mediaTime = 0;
    int64_t duration = 0;
    ASSERT_FALSE(timeline.SubmitFrame(-1, &mediaTime, &duration));
    ASSERT_EQ(timeline.FramesSubmitted(), 1);
    ASSERT_EQ(timeline.FramesDropped(), 1);
});

TEST_REGISTRAR(FrameTimelinePreservesLargeGapForBoundedFinalize, []() {
    FrameTimeline timeline(30);
    int64_t mediaTime = 0;
    int64_t duration = 0;
    ASSERT_TRUE(timeline.SubmitFrame(0, &mediaTime, &duration));
    ASSERT_TRUE(timeline.SubmitFrame(kHnsPerSecond + kHnsPerSecond / 2, &mediaTime, &duration));
    ASSERT_EQ(mediaTime, kHnsPerSecond + kHnsPerSecond / 2);
    ASSERT_EQ(duration, kHnsPerSecond + kHnsPerSecond / 2);
    ASSERT_TRUE(timeline.FinalizeAt(2 * kHnsPerSecond, &mediaTime, &duration));
    ASSERT_EQ(mediaTime, kHnsPerSecond + kHnsPerSecond / 2);
    ASSERT_EQ(duration, kHnsPerSecond / 2);
});

TEST_REGISTRAR(FrameTimelineRejectsCaptureEndWithoutAdvancingLastSample, []() {
    FrameTimeline timeline(30);
    int64_t mediaTime = 0;
    int64_t duration = 0;
    constexpr int64_t captureEnd = 100'080'000LL;

    ASSERT_TRUE(timeline.SubmitFrame(0, &mediaTime, &duration, captureEnd));
    ASSERT_TRUE(timeline.SubmitFrame(91'750'000LL, &mediaTime, &duration, captureEnd));
    ASSERT_FALSE(timeline.SubmitFrame(captureEnd, &mediaTime, &duration, captureEnd));

    ASSERT_EQ(timeline.FramesDropped(), 1);
    ASSERT_TRUE(timeline.FinalizeAt(captureEnd, &mediaTime, &duration));
    ASSERT_EQ(mediaTime, 91'750'000LL);
    ASSERT_EQ(duration, 8'330'000LL);
});

TEST_REGISTRAR(FrameTimelineResetClearsState, []() {
    FrameTimeline timeline(30);
    int64_t mediaTime = 0;
    int64_t duration = 0;
    ASSERT_TRUE(timeline.SubmitFrame(kHnsPerSecond, &mediaTime, &duration));
    timeline.Reset();
    ASSERT_EQ(timeline.FramesSubmitted(), 0);
    ASSERT_EQ(timeline.FramesDropped(), 0);
    ASSERT_TRUE(timeline.SubmitFrame(kHnsPerSecond / 2, &mediaTime, &duration));
    ASSERT_EQ(mediaTime, 0);
});
