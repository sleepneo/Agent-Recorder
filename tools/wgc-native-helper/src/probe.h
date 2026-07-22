#pragma once

#include <string>

namespace wgc {

struct ProbeResult {
    bool ok = false;
    std::string error;
    bool wgcSupported = false;
    bool d3d11Initialized = false;
    bool encoderCreated = false;
};

// Performs a non-capturing probe: checks OS support, D3D11 initialization, and
// the ability to create a software H.264 encoder. Does not create a capture item
// or call StartCapture.
ProbeResult RunProbe();

} // namespace wgc