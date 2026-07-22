#pragma once

#include <cstdint>

namespace wgc {

// Computes encoder-safe capture dimensions from the source display size.
// Returns false if the source is too small to produce a valid H.264 frame.
// On success, captureWidth/captureHeight are even, >= 32, and <= source.
bool ComputeCaptureDimensions(int sourceWidth,
                              int sourceHeight,
                              int& captureWidth,
                              int& captureHeight);

// Returns true if the observed content size differs from the expected size.
bool IsContentSizeChanged(int expectedWidth,
                          int expectedHeight,
                          int actualWidth,
                          int actualHeight);

// Ensures dimensions are even and >= 32 for the encoder.
void NormalizeEncoderDimensions(int& width, int& height);

} // namespace wgc
