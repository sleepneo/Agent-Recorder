#pragma once

#include "options.h"

#include <windows.h>
#include <limits>

namespace wgc {

// Returns true if monitor bounds (from EnumDisplayMonitors) exactly match the
// target rectangle requested by the caller. No scaling, rounding, or tolerance
// is applied; every edge must match exactly. Both coordinate spaces must be
// physical pixels (the caller is responsible for Per-Monitor V2 awareness).
inline bool RectExactlyMatchesMonitor(const Rect& target, const RECT& monitorBounds) {
    return monitorBounds.left == target.x &&
           monitorBounds.top == target.y &&
           (monitorBounds.right - monitorBounds.left) == target.width &&
           (monitorBounds.bottom - monitorBounds.top) == target.height;
}

// Validates a region against the complete display rectangle and returns the
// relative crop origin used by the D3D11_BOX copy. Widened arithmetic keeps
// hostile virtual-screen coordinates from wrapping at the int boundary.
inline bool TryGetRegionCrop(const Rect& display,
                             const Rect& region,
                             int& offsetX,
                             int& offsetY) {
    offsetX = 0;
    offsetY = 0;
    constexpr int kMinimumDimension = 32;
    if (display.width <= 0 || display.height <= 0 ||
        region.width < kMinimumDimension || region.height < kMinimumDimension ||
        (region.width % 2) != 0 || (region.height % 2) != 0) {
        return false;
    }

    const long long displayRight = static_cast<long long>(display.x) + display.width;
    const long long displayBottom = static_cast<long long>(display.y) + display.height;
    const long long regionRight = static_cast<long long>(region.x) + region.width;
    const long long regionBottom = static_cast<long long>(region.y) + region.height;
    const long long cropX = static_cast<long long>(region.x) - display.x;
    const long long cropY = static_cast<long long>(region.y) - display.y;

    if (region.x < display.x || region.y < display.y ||
        regionRight > displayRight || regionBottom > displayBottom ||
        cropX < 0 || cropY < 0 ||
        cropX > std::numeric_limits<int>::max() ||
        cropY > std::numeric_limits<int>::max()) {
        return false;
    }

    offsetX = static_cast<int>(cropX);
    offsetY = static_cast<int>(cropY);
    return true;
}

} // namespace wgc
