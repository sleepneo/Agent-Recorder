#pragma once

#include "options.h"

#include <cstdint>
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
    bool windowCaptureSupported = false;
    bool d3d11Initialized = false;
    bool encoderCreated = false;
    bool hardwareH264Available = false;
    std::uint32_t hardwareH264CandidateCount = 0;
    std::uint32_t hardwareH264ActivationFailureCount = 0;
    std::uint32_t hardwareH264ShutdownFailureCount = 0;
    std::string dpiAwareness = "unknown";
    std::vector<ProbeMonitorInfo> monitors;
};

struct HardwareH264EnumerationEvidence {
    bool enumerationSucceeded = false;
    std::uint32_t returnedCandidateCount = 0;
    std::uint32_t activationSuccessCount = 0;
    std::uint32_t activatedCandidateCount = 0;
    std::uint32_t activationFailureCount = 0;
    std::uint32_t shutdownFailureCount = 0;
};

// Candidate evidence is capability-only. A candidate is counted only after
// activation as an IMFTransform; enumeration success with zero activations,
// API failure, and partial activation are all represented explicitly.
inline bool IsHardwareH264Available(const HardwareH264EnumerationEvidence& evidence) {
    return evidence.enumerationSucceeded && evidence.activatedCandidateCount > 0;
}

// Shared prerequisites used by display selection. Window interop is reported
// separately and is intentionally not part of this predicate.
inline bool HasSharedCaptureCapabilities(const ProbeResult& result) {
    return result.wgcSupported && result.d3d11Initialized && result.encoderCreated;
}

// Performs a non-capturing probe: checks OS support, D3D11 initialization, the
// ability to create a software H.264 encoder, and enumerates monitors in the
// current DPI awareness context. Does not create a capture item or call
// StartCapture.
ProbeResult RunProbe();

} // namespace wgc
