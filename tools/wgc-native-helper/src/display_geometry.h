#pragma once

#include "options.h"

#include <windows.h>

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

} // namespace wgc
