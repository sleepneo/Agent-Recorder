#pragma once

#include "options.h"

#include <string>
#include <vector>

namespace wgc {

struct ProbeMonitorInfo {
    Rect bounds;
    bool primary = false;
};

struct ProbeResult {
    bool ok = false;
    std::string error;
    bool wgcSupported = false;
    bool d3d11Initialized = false;
    bool encoderCreated = false;
    std::string dpiAwareness = "unknown";
    std::vector<ProbeMonitorInfo> monitors;
};

// Performs a non-capturing probe: checks OS support, D3D11 initialization, the
// ability to create a software H.264 encoder, and enumerates monitors in the
// current DPI awareness context. Does not create a capture item or call
// StartCapture.
ProbeResult RunProbe();

} // namespace wgc