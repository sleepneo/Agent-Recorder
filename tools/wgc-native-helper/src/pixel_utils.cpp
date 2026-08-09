#include "pixel_utils.h"

#include <limits>

namespace wgc {

std::vector<uint8_t> CopyBgraToRgb32(const std::vector<uint8_t>& bgra,
                                     int width,
                                     int height) {
    if (width <= 0 || height <= 0) {
        return {};
    }

    // Guard against overflow in the size calculation.
    constexpr size_t kMaxSize = static_cast<size_t>(std::numeric_limits<int>::max());
    const size_t pixelCount64 = static_cast<size_t>(width) * static_cast<size_t>(height);
    if (pixelCount64 > kMaxSize / 4) {
        return {};
    }
    const size_t expected = pixelCount64 * 4;
    if (bgra.size() < expected) {
        return {};
    }

    std::vector<uint8_t> rgb32(expected);
    const size_t pixelCount = static_cast<size_t>(width) * static_cast<size_t>(height);
    for (size_t i = 0; i < pixelCount; ++i) {
        const size_t src = i * 4;
        const size_t dst = i * 4;
        rgb32[dst + 0] = bgra[src + 0]; // B
        rgb32[dst + 1] = bgra[src + 1]; // G
        rgb32[dst + 2] = bgra[src + 2]; // R
        rgb32[dst + 3] = 0;             // X (alpha ignored, zeroed)
    }
    return rgb32;
}

} // namespace wgc
