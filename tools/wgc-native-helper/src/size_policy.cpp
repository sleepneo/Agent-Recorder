#include "size_policy.h"

#include <algorithm>
#include <cmath>

namespace wgc {

namespace {

constexpr int kMinEncoderDimension = 32;

int MakeEvenAndClamp(int value) {
    if (value < kMinEncoderDimension) return 0;
    if (value % 2 != 0) --value;
    return value;
}

} // namespace

bool ComputeCaptureDimensions(int sourceWidth,
                              int sourceHeight,
                              int& captureWidth,
                              int& captureHeight) {
    captureWidth = MakeEvenAndClamp(sourceWidth);
    captureHeight = MakeEvenAndClamp(sourceHeight);
    return captureWidth >= kMinEncoderDimension && captureHeight >= kMinEncoderDimension;
}

bool IsContentSizeChanged(int expectedWidth,
                          int expectedHeight,
                          int actualWidth,
                          int actualHeight) {
    return actualWidth != expectedWidth || actualHeight != expectedHeight;
}

void NormalizeEncoderDimensions(int& width, int& height) {
    if (width < kMinEncoderDimension) width = kMinEncoderDimension;
    if (height < kMinEncoderDimension) height = kMinEncoderDimension;
    if (width % 2 != 0) --width;
    if (height % 2 != 0) --height;
}

} // namespace wgc
