#include "test_framework.h"

#include "size_policy.h"

using namespace wgc;

TEST_REGISTRAR(ComputeCaptureDimensionsEvenSizeOk, []() {
    int w = 0, h = 0;
    ASSERT_TRUE(ComputeCaptureDimensions(1920, 1080, w, h));
    ASSERT_EQ(w, 1920);
    ASSERT_EQ(h, 1080);
});

TEST_REGISTRAR(ComputeCaptureDimensionsOddSizeRoundedDown, []() {
    int w = 0, h = 0;
    ASSERT_TRUE(ComputeCaptureDimensions(1921, 1079, w, h));
    ASSERT_EQ(w, 1920);
    ASSERT_EQ(h, 1078);
});

TEST_REGISTRAR(ComputeCaptureDimensionsTooSmallFails, []() {
    int w = 0, h = 0;
    ASSERT_FALSE(ComputeCaptureDimensions(31, 1080, w, h));
    ASSERT_FALSE(ComputeCaptureDimensions(1920, 31, w, h));
    ASSERT_FALSE(ComputeCaptureDimensions(16, 16, w, h));
});

TEST_REGISTRAR(ComputeCaptureDimensionsMinimumOk, []() {
    int w = 0, h = 0;
    ASSERT_TRUE(ComputeCaptureDimensions(32, 32, w, h));
    ASSERT_EQ(w, 32);
    ASSERT_EQ(h, 32);
});

TEST_REGISTRAR(IsContentSizeChangedDetectsChange, []() {
    ASSERT_FALSE(IsContentSizeChanged(1920, 1080, 1920, 1080));
    ASSERT_TRUE(IsContentSizeChanged(1920, 1080, 1920, 1081));
    ASSERT_TRUE(IsContentSizeChanged(1920, 1080, 1919, 1080));
});

TEST_REGISTRAR(NormalizeEncoderDimensionsEnforcesMinimumAndEven, []() {
    int w = 31, h = 31;
    NormalizeEncoderDimensions(w, h);
    ASSERT_EQ(w, 32);
    ASSERT_EQ(h, 32);

    w = 33; h = 33;
    NormalizeEncoderDimensions(w, h);
    ASSERT_EQ(w, 32);
    ASSERT_EQ(h, 32);

    w = 64, h = 64;
    NormalizeEncoderDimensions(w, h);
    ASSERT_EQ(w, 64);
    ASSERT_EQ(h, 64);
});
