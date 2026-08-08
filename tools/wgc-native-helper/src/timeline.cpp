#include "timeline.h"

#include <algorithm>
#include <cmath>

namespace wgc {

namespace {

constexpr int64_t kHnsPerSecond = 10'000'000LL;
} // namespace

FrameTimeline::FrameTimeline(int fps) : fps_(fps) {
    if (fps_ <= 0) fps_ = 30;
    nominalDurationHns_ = kHnsPerSecond / fps_;
    if (nominalDurationHns_ <= 0) nominalDurationHns_ = 1;
}

bool FrameTimeline::SubmitFrame(int64_t systemRelativeTimeHns,
                                int64_t* mediaTimeHns,
                                int64_t* durationHns,
                                int64_t maxMediaTimeHns) {
    ++submitted_;

    if (systemRelativeTimeHns < 0) {
        ++dropped_;
        return false;
    }

    if (!hasAnchor_) {
        anchorHns_ = systemRelativeTimeHns;
        hasAnchor_ = true;
        lastMediaTimeHns_ = 0;
        *mediaTimeHns = 0;
        *durationHns = nominalDurationHns_;
        if (maxMediaTimeHns >= 0 && *mediaTimeHns >= maxMediaTimeHns) {
            hasAnchor_ = false;
            anchorHns_ = 0;
            lastMediaTimeHns_ = 0;
            ++dropped_;
            return false;
        }
        return true;
    }

    int64_t candidate = systemRelativeTimeHns - anchorHns_;
    if (candidate <= lastMediaTimeHns_) {
        // Non-monotonic or duplicate timestamp: drop the frame.
        ++dropped_;
        return false;
    }

    if (maxMediaTimeHns >= 0 && candidate >= maxMediaTimeHns) {
        // Do not advance lastMediaTimeHns_ for a frame outside the authorized
        // media window; a pending final sample must remain finalizable.
        ++dropped_;
        return false;
    }

    int64_t frameDuration = candidate - lastMediaTimeHns_;
    lastMediaTimeHns_ = candidate;
    *mediaTimeHns = candidate;
    *durationHns = frameDuration;
    return true;
}

bool FrameTimeline::FinalizeAt(int64_t captureEndHns,
                               int64_t* mediaTimeHns,
                               int64_t* durationHns) const {
    if (!hasAnchor_ || captureEndHns <= lastMediaTimeHns_) {
        return false;
    }

    *mediaTimeHns = lastMediaTimeHns_;
    *durationHns = captureEndHns - lastMediaTimeHns_;
    return *durationHns > 0;
}

void FrameTimeline::Reset() {
    hasAnchor_ = false;
    anchorHns_ = 0;
    lastMediaTimeHns_ = 0;
    submitted_ = 0;
    dropped_ = 0;
}

} // namespace wgc
